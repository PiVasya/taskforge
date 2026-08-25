using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using TaskForge.Tasks.Api.Contracts;
using TaskForge.Tasks.Api.Data;
using static TaskForge.Tasks.Api.Services.Access.AssignmentApiAccessService;
using static TaskForge.Tasks.Api.Services.Common.AssignmentApiCommonService;
using static TaskForge.Tasks.Api.Services.Mapping.AssignmentApiMappingService;
using static TaskForge.Tasks.Api.Services.Results.AssignmentApiResultsService;

namespace TaskForge.Tasks.Api.Services.Access;

internal sealed class CourseMapProjectionService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private const int SegmentNodeChunkSize = 160;
    private const int SegmentEdgeChunkSize = 320;

    private static readonly ConcurrentDictionary<string, Lazy<Task<CourseMapSnapshot?>>> SnapshotBuilds = new();

    private readonly TasksDbContext _db;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHttpClientFactory _clients;
    private readonly IConfiguration _cfg;
    private readonly IDistributedCache _cache;
    private readonly IMemoryCache _memory;
    private readonly ILogger<CourseMapProjectionService> _logger;

    public CourseMapProjectionService(
        TasksDbContext db,
        IServiceScopeFactory scopeFactory,
        IHttpClientFactory clients,
        IConfiguration cfg,
        IDistributedCache cache,
        IMemoryCache memory,
        ILogger<CourseMapProjectionService> logger)
    {
        _db = db;
        _scopeFactory = scopeFactory;
        _clients = clients;
        _cfg = cfg;
        _cache = cache;
        _memory = memory;
        _logger = logger;
    }

    internal sealed record AssignmentCard(
        Guid Id,
        Guid CourseId,
        string Title,
        string Type,
        string Language,
        string? Tags,
        string? Description,
        bool IsVisible,
        int Sort,
        bool SolvedByCurrentUser);

    internal sealed record CourseCard(
        Guid Id,
        Guid? ParentCourseId,
        string Title,
        string? Description,
        bool IsPublic,
        bool IsHiddenFromStudents,
        int Sort);

    internal sealed record CourseProgressCard(int Total, int Solved, int Percent);

    internal sealed record SegmentPayload(
        Guid CourseId,
        JsonElement[] Nodes,
        JsonElement[] Edges,
        AssignmentCard[] Assignments,
        CourseCard[] Courses,
        Dictionary<string, CourseProgressCard> CourseProgress);

    internal sealed record SessionMeta(
        string ProjectionToken,
        Guid RootCourseId,
        Guid RequestedCourseId,
        int Version,
        long ProjectionRevision,
        JsonElement Viewport,
        int VisibleNodeCount,
        int VisibleEdgeCount,
        DateTimeOffset? UpdatedAt,
        Guid? UpdatedBy);

    internal sealed record SessionResult(SessionMeta Meta, IEnumerable<SegmentPayload> Segments);

    internal sealed record DeltaRequest(string? ProjectionToken, Guid? ChangedAssignmentId);

    internal sealed record DeltaResult(
        bool ResetRequired,
        string? ProjectionToken,
        long ProjectionRevision,
        int Version,
        JsonElement[] NodesAdded,
        string[] NodeIdsRemoved,
        JsonElement[] EdgesAdded,
        string[] EdgeIdsRemoved,
        AssignmentCard[] AssignmentsChanged,
        CourseCard[] CoursesChanged,
        Dictionary<string, CourseProgressCard> CourseProgress,
        Guid[] OpenedCourseIds);

    internal async Task<SessionResult?> CreateSessionAsync(
        Guid requestedCourseId,
        Guid userId,
        bool bypassStudentVisibility,
        bool forceFresh,
        CancellationToken ct)
    {
        var startedAt = DateTimeOffset.UtcNow;
        _logger.LogInformation(
            "TFDBG MAP SESSION START requested={RequestedCourseId} user={UserId} bypass={Bypass} fresh={Fresh}",
            requestedCourseId,
            userId,
            bypassStudentVisibility,
            forceFresh);

        var snapshot = await GetSnapshotAsync(requestedCourseId, ct, forceFresh: forceFresh);
        if (snapshot is null)
        {
            _logger.LogWarning("TFDBG MAP SESSION MISS requested={RequestedCourseId} user={UserId} stage=snapshot", requestedCourseId, userId);
            return null;
        }

        var evaluation = await EvaluateAsync(snapshot, requestedCourseId, userId, bypassStudentVisibility, solvedIds: null, ct);
        if (evaluation is null)
        {
            _logger.LogWarning("TFDBG MAP SESSION MISS requested={RequestedCourseId} user={UserId} stage=evaluation version={Version}", requestedCourseId, userId, snapshot.Version);
            return null;
        }

        var state = BuildProjectionState(snapshot, evaluation, userId, bypassStudentVisibility);
        var token = Guid.NewGuid().ToString("N");
        state = state with { ProjectionToken = token, ProjectionRevision = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
        await SaveProjectionStateAsync(state, ct);
        await SaveCurrentProjectionPointerAsync(state, ct);

        var segments = BuildSegments(snapshot, evaluation, requestedCourseId);
        var viewport = ReadViewport(evaluation.LearningDocument);
        var meta = new SessionMeta(
            state.ProjectionToken,
            snapshot.RootCourseId,
            requestedCourseId,
            snapshot.Version,
            state.ProjectionRevision,
            viewport,
            state.VisibleNodeIds.Length,
            state.VisibleEdgeIds.Length,
            snapshot.UpdatedAt,
            snapshot.UpdatedBy);

        _logger.LogInformation(
            "TFDBG MAP SESSION READY requested={RequestedCourseId} root={RootCourseId} user={UserId} version={Version} revision={Revision} visibleNodes={VisibleNodes} visibleEdges={VisibleEdges} visibleCourses={VisibleCourses} visibleAssignments={VisibleAssignments} solved={Solved} durationMs={DurationMs:F2}",
            requestedCourseId,
            snapshot.RootCourseId,
            userId,
            snapshot.Version,
            state.ProjectionRevision,
            state.VisibleNodeIds.Length,
            state.VisibleEdgeIds.Length,
            state.VisibleCourseIds.Length,
            state.VisibleAssignmentIds.Length,
            state.SolvedAssignmentIds.Length,
            (DateTimeOffset.UtcNow - startedAt).TotalMilliseconds);

        return new SessionResult(meta, segments);
    }

    internal async Task<DeltaResult?> CreateDeltaAsync(
        Guid requestedCourseId,
        Guid userId,
        bool bypassStudentVisibility,
        DeltaRequest request,
        CancellationToken ct)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var token = (request.ProjectionToken ?? string.Empty).Trim();
        _logger.LogInformation(
            "TFDBG MAP DELTA START requested={RequestedCourseId} user={UserId} bypass={Bypass} token={TokenPrefix} changedAssignment={ChangedAssignmentId}",
            requestedCourseId,
            userId,
            bypassStudentVisibility,
            token.Length > 10 ? token[..10] : token,
            request.ChangedAssignmentId);
        if (token.Length == 0)
        {
            _logger.LogWarning("TFDBG MAP DELTA RESET requested={RequestedCourseId} user={UserId} reason=missing-token", requestedCourseId, userId);
            return ResetDelta();
        }

        var previous = await LoadProjectionStateAsync(token, ct);
        if (previous is null
            || previous.UserId != userId
            || previous.RequestedCourseId != requestedCourseId
            || previous.BypassStudentVisibility != bypassStudentVisibility)
        {
            _logger.LogWarning(
                "TFDBG MAP DELTA RESET requested={RequestedCourseId} user={UserId} reason=projection-state-mismatch found={Found}",
                requestedCourseId,
                userId,
                previous is not null);
            return ResetDelta();
        }

        var snapshot = await GetSnapshotAsync(requestedCourseId, ct, forceFreshMeta: true);
        if (snapshot is null) return null;
        if (snapshot.RootCourseId != previous.RootCourseId || snapshot.Version != previous.Version)
        {
            _logger.LogWarning(
                "TFDBG MAP DELTA RESET requested={RequestedCourseId} user={UserId} reason=map-version-changed previousRoot={PreviousRoot} nextRoot={NextRoot} previousVersion={PreviousVersion} nextVersion={NextVersion}",
                requestedCourseId,
                userId,
                previous.RootCourseId,
                snapshot.RootCourseId,
                previous.Version,
                snapshot.Version);
            return ResetDelta(snapshot.Version);
        }

        var hasChangedAssignment = request.ChangedAssignmentId.HasValue && request.ChangedAssignmentId.Value != Guid.Empty;
        var replayState = hasChangedAssignment
            ? null
            : await LoadNewerCurrentProjectionAsync(previous, ct);

        // Progress can change immediately after a successful solution while the
        // browser is navigating back to the course. Neither a recently verified
        // projection nor a merely newer projection pointer proves that learner
        // progress is current: either may have been built just before the solve.
        HashSet<Guid>? solved = null;
        HashSet<Guid>? knownAccessibleCourseIds = null;

        if (replayState is not null)
        {
            // Preserve immutable projection replay, but only after validating that
            // the replay candidate still matches authoritative access + solved state.
            // This keeps fast multi-tab/back navigation without replaying stale progress.
            var allCourseIds = snapshot.Tree.CourseIds.Where(x => x != Guid.Empty).Distinct().ToArray();
            var authoritativeAccessibleCourseIds = bypassStudentVisibility
                ? allCourseIds.ToHashSet()
                : await LoadAccessibleCourseIdsAsync(allCourseIds, userId, _clients, _cfg, ct);
            var relevantAssignmentIds = snapshot.Assignments
                .Where(x => authoritativeAccessibleCourseIds.Contains(x.CourseId) && (bypassStudentVisibility || x.IsVisible))
                .Select(x => x.Id)
                .ToArray();
            var authoritativeSolved = await LoadSolvedAssignmentIdsAsync(userId, relevantAssignmentIds, _db, _clients, _cfg, ct);
            var replayMatches = authoritativeAccessibleCourseIds.SetEquals(replayState.AccessibleCourseIds)
                && authoritativeSolved.SetEquals(replayState.SolvedAssignmentIds);

            if (replayMatches)
            {
                solved = replayState.SolvedAssignmentIds.ToHashSet();
                knownAccessibleCourseIds = replayState.AccessibleCourseIds.ToHashSet();
            }
            else
            {
                _logger.LogInformation(
                    "TFDBG MAP DELTA REPLAY REJECT requested={RequestedCourseId} user={UserId} revision={Revision} reason=authoritative-progress-changed",
                    requestedCourseId,
                    userId,
                    replayState.ProjectionRevision);
                replayState = null;
                solved = authoritativeSolved;
                knownAccessibleCourseIds = authoritativeAccessibleCourseIds;
            }
        }
        else if (hasChangedAssignment)
        {
            // The solve page tells us exactly which assignment changed. Verify only
            // that id so the common solve -> next/course path stays cheap and fresh.
            solved = previous.SolvedAssignmentIds.ToHashSet();
            var changedId = request.ChangedAssignmentId.Value;
            var authoritative = await LoadSolvedAssignmentIdsAsync(userId, new[] { changedId }, _db, _clients, _cfg, ct);
            if (authoritative.Contains(changedId)) solved.Add(changedId);
            else solved.Remove(changedId);
            knownAccessibleCourseIds = previous.AccessibleCourseIds.ToHashSet();
        }
        // No explicit change and no replay candidate: EvaluateAsync must reload the
        // complete authoritative access + solved state. There is deliberately no
        // time-based "recently verified" shortcut here.

        var evaluation = await EvaluateAsync(snapshot, requestedCourseId, userId, bypassStudentVisibility, solved, ct, knownAccessibleCourseIds);
        if (evaluation is null) return null;

        var next = BuildProjectionState(snapshot, evaluation, userId, bypassStudentVisibility) with
        {
            ProjectionToken = replayState?.ProjectionToken ?? Guid.NewGuid().ToString("N"),
            ProjectionRevision = replayState?.ProjectionRevision
                ?? System.Math.Max(previous.ProjectionRevision + 1, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
        };

        var oldNodeIds = previous.VisibleNodeIds.ToHashSet(StringComparer.Ordinal);
        var oldEdgeIds = previous.VisibleEdgeIds.ToHashSet(StringComparer.Ordinal);
        var nextNodeIds = next.VisibleNodeIds.ToHashSet(StringComparer.Ordinal);
        var nextEdgeIds = next.VisibleEdgeIds.ToHashSet(StringComparer.Ordinal);

        var nodesAddedIds = nextNodeIds.Except(oldNodeIds, StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        var edgesAddedIds = nextEdgeIds.Except(oldEdgeIds, StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        var nodeIdsRemoved = oldNodeIds.Except(nextNodeIds, StringComparer.Ordinal).ToArray();
        var edgeIdsRemoved = oldEdgeIds.Except(nextEdgeIds, StringComparer.Ordinal).ToArray();

        var document = evaluation.LearningDocument;
        var nodesAdded = ReadArray(document, "nodes")
            .Where(x => GetId(x) is string id && nodesAddedIds.Contains(id))
            .ToArray();
        var edgesAdded = ReadArray(document, "edges")
            .Where(x => GetId(x) is string id && edgesAddedIds.Contains(id))
            .ToArray();

        var entityIds = nodesAdded
            .Select(ReadEntityId)
            .Where(x => x.HasValue)
            .Select(x => x!.Value)
            .ToHashSet();
        var solvedChangedIds = previous.SolvedAssignmentIds
            .Except(next.SolvedAssignmentIds)
            .Concat(next.SolvedAssignmentIds.Except(previous.SolvedAssignmentIds))
            .ToHashSet();
        entityIds.UnionWith(solvedChangedIds);

        var previousVisibleCourses = previous.VisibleCourseIds.ToHashSet();
        var openedCourseIds = next.VisibleCourseIds
            .Where(x => !previousVisibleCourses.Contains(x))
            .ToArray();
        entityIds.UnionWith(openedCourseIds);

        var assignmentCards = BuildAssignmentCards(snapshot, evaluation.SolvedAssignmentIds, entityIds);
        var courseCards = BuildCourseCards(snapshot, entityIds);
        var progress = next.CourseProgress
            .Where(x => !previous.CourseProgress.TryGetValue(x.Key, out var oldValue) || oldValue != x.Value)
            .ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);

        if (replayState is null)
        {
            await SaveProjectionStateAsync(next, ct);
            await SaveCurrentProjectionPointerAsync(next, ct);
        }

        _logger.LogInformation(
            "TFDBG MAP DELTA READY requested={RequestedCourseId} user={UserId} replay={Replay} version={Version} revision={Revision} addNodes={AddNodes} removeNodes={RemoveNodes} addEdges={AddEdges} removeEdges={RemoveEdges} assignmentChanges={AssignmentChanges} courseChanges={CourseChanges} openedCourses={OpenedCourses} durationMs={DurationMs:F2}",
            requestedCourseId,
            userId,
            replayState is not null,
            next.Version,
            next.ProjectionRevision,
            nodesAdded.Length,
            nodeIdsRemoved.Length,
            edgesAdded.Length,
            edgeIdsRemoved.Length,
            assignmentCards.Length,
            courseCards.Length,
            openedCourseIds.Length,
            (DateTimeOffset.UtcNow - startedAt).TotalMilliseconds);

        return new DeltaResult(
            false,
            next.ProjectionToken,
            next.ProjectionRevision,
            next.Version,
            nodesAdded,
            nodeIdsRemoved,
            edgesAdded,
            edgeIdsRemoved,
            assignmentCards,
            courseCards,
            progress,
            openedCourseIds);
    }

    private async Task<ProjectionState?> LoadNewerCurrentProjectionAsync(ProjectionState previous, CancellationToken ct)
    {
        var pointerKey = CurrentProjectionPointerKey(
            previous.UserId,
            previous.RootCourseId,
            previous.RequestedCourseId,
            previous.Version,
            previous.BypassStudentVisibility);
        var tokenBytes = await SafeGetAsync(pointerKey, ct);
        if (tokenBytes is null || tokenBytes.Length == 0) return null;
        var token = Encoding.UTF8.GetString(tokenBytes);
        if (string.Equals(token, previous.ProjectionToken, StringComparison.Ordinal)) return null;
        var state = await LoadProjectionStateAsync(token, ct);
        if (state is null
            || state.UserId != previous.UserId
            || state.RootCourseId != previous.RootCourseId
            || state.RequestedCourseId != previous.RequestedCourseId
            || state.Version != previous.Version
            || state.BypassStudentVisibility != previous.BypassStudentVisibility
            || state.ProjectionRevision <= previous.ProjectionRevision)
        {
            return null;
        }
        return state;
    }

    internal async Task<bool?> TryGetCachedAssignmentAccessAsync(
        Guid assignmentCourseId,
        Guid assignmentId,
        Guid userId,
        bool bypassStudentVisibility,
        CancellationToken ct)
    {
        var meta = await GetMapMetaAsync(assignmentCourseId, ct);
        if (meta is null || meta.RootCourseId == Guid.Empty) return null;

        var pointerKeys = new[]
        {
            CurrentProjectionPointerKey(userId, meta.RootCourseId, meta.RootCourseId, meta.Version, bypassStudentVisibility),
            CurrentProjectionPointerKey(userId, meta.RootCourseId, assignmentCourseId, meta.Version, bypassStudentVisibility)
        }.Distinct(StringComparer.Ordinal).ToArray();

        ProjectionState? newest = null;
        foreach (var pointerKey in pointerKeys)
        {
            var tokenBytes = await SafeGetAsync(pointerKey, ct);
            if (tokenBytes is null || tokenBytes.Length == 0) continue;
            var state = await LoadProjectionStateAsync(Encoding.UTF8.GetString(tokenBytes), ct);
            if (state is null
                || state.UserId != userId
                || state.RootCourseId != meta.RootCourseId
                || state.Version != meta.Version
                || state.BypassStudentVisibility != bypassStudentVisibility)
            {
                continue;
            }
            if (newest is null || state.ProjectionRevision > newest.ProjectionRevision) newest = state;
        }

        return newest is null ? null : newest.VisibleAssignmentIds.Contains(assignmentId);
    }

    private async Task<CourseMapProgressionService.Evaluation?> EvaluateAsync(
        CourseMapSnapshot snapshot,
        Guid requestedCourseId,
        Guid userId,
        bool bypassStudentVisibility,
        HashSet<Guid>? solvedIds,
        CancellationToken ct,
        HashSet<Guid>? knownAccessibleCourseIds = null)
    {
        var startedAt = DateTimeOffset.UtcNow;
        _logger.LogInformation(
            "TFDBG MAP EVAL START requested={RequestedCourseId} root={RootCourseId} user={UserId} version={Version} bypass={Bypass} knownAccess={KnownAccess} suppliedSolved={SuppliedSolved}",
            requestedCourseId,
            snapshot.RootCourseId,
            userId,
            snapshot.Version,
            bypassStudentVisibility,
            knownAccessibleCourseIds?.Count,
            solvedIds?.Count);
        var allCourseIds = snapshot.Tree.CourseIds.Where(x => x != Guid.Empty).Distinct().ToArray();
        var accessibleCourseIds = knownAccessibleCourseIds ?? (bypassStudentVisibility
            ? allCourseIds.ToHashSet()
            : await LoadAccessibleCourseIdsAsync(allCourseIds, userId, _clients, _cfg, ct));

        if (!accessibleCourseIds.Contains(requestedCourseId))
        {
            _logger.LogWarning(
                "TFDBG MAP EVAL DENY requested={RequestedCourseId} user={UserId} accessibleCourses={AccessibleCourses} durationMs={DurationMs:F2}",
                requestedCourseId,
                userId,
                accessibleCourseIds.Count,
                (DateTimeOffset.UtcNow - startedAt).TotalMilliseconds);
            return null;
        }

        var assignments = snapshot.Assignments
            .Where(x => accessibleCourseIds.Contains(x.CourseId) && (bypassStudentVisibility || x.IsVisible))
            .ToList();
        solvedIds ??= await LoadSolvedAssignmentIdsAsync(userId, assignments.Select(x => x.Id), _db, _clients, _cfg, ct);

        if (snapshot.Document is null || snapshot.Document.Value.ValueKind != JsonValueKind.Object)
        {
            var fallback = BuildFallbackDocument(snapshot, requestedCourseId, accessibleCourseIds, assignments, solvedIds);
            var fallbackEvaluation = CourseMapProgressionService.EvaluatePrepared(
                snapshot.RootCourseId,
                requestedCourseId,
                snapshot.Version,
                fallback,
                snapshot.Tree,
                accessibleCourseIds,
                assignments,
                solvedIds,
                snapshot.UpdatedAt,
                snapshot.UpdatedBy);
            _logger.LogInformation(
                "TFDBG MAP EVAL END requested={RequestedCourseId} user={UserId} fallback=true accessibleCourses={AccessibleCourses} assignments={Assignments} solved={Solved} visibleCourses={VisibleCourses} visibleAssignments={VisibleAssignments} durationMs={DurationMs:F2}",
                requestedCourseId,
                userId,
                accessibleCourseIds.Count,
                assignments.Count,
                solvedIds.Count,
                fallbackEvaluation.VisibleCourseIds.Count,
                fallbackEvaluation.VisibleAssignmentIds.Count,
                (DateTimeOffset.UtcNow - startedAt).TotalMilliseconds);
            return fallbackEvaluation;
        }

        var evaluation = CourseMapProgressionService.EvaluatePrepared(
            snapshot.RootCourseId,
            requestedCourseId,
            snapshot.Version,
            snapshot.Document.Value,
            snapshot.Tree,
            accessibleCourseIds,
            assignments,
            solvedIds,
            snapshot.UpdatedAt,
            snapshot.UpdatedBy);

        var visible = evaluation.VisibleCourseIds.Contains(requestedCourseId);
        _logger.LogInformation(
            "TFDBG MAP EVAL END requested={RequestedCourseId} user={UserId} fallback=false allowed={Allowed} accessibleCourses={AccessibleCourses} assignments={Assignments} solved={Solved} visibleCourses={VisibleCourses} visibleAssignments={VisibleAssignments} durationMs={DurationMs:F2}",
            requestedCourseId,
            userId,
            visible,
            accessibleCourseIds.Count,
            assignments.Count,
            solvedIds.Count,
            evaluation.VisibleCourseIds.Count,
            evaluation.VisibleAssignmentIds.Count,
            (DateTimeOffset.UtcNow - startedAt).TotalMilliseconds);
        return visible ? evaluation : null;
    }

    private async Task<CourseMapSnapshot?> GetSnapshotAsync(
        Guid requestedCourseId,
        CancellationToken ct,
        bool forceFresh = false,
        bool forceFreshMeta = false)
    {
        var meta = await GetMapMetaAsync(requestedCourseId, ct, forceFresh || forceFreshMeta);
        if (meta is null || meta.RootCourseId == Guid.Empty)
        {
            _logger.LogWarning("TFDBG MAP SNAPSHOT MISS requested={RequestedCourseId} stage=meta", requestedCourseId);
            return null;
        }
        var key = SnapshotKey(meta.RootCourseId, meta.Version);

        if (forceFresh)
        {
            _logger.LogInformation(
                "TFDBG MAP SNAPSHOT FORCE requested={RequestedCourseId} root={RootCourseId} version={Version}",
                requestedCourseId,
                meta.RootCourseId,
                meta.Version);
            return await BuildAndCacheSnapshotAsync(meta, key);
        }

        if (_memory.TryGetValue<CourseMapSnapshot>(key, out var memorySnapshot) && memorySnapshot is not null)
        {
            _logger.LogInformation("TFDBG MAP SNAPSHOT HIT requested={RequestedCourseId} root={RootCourseId} version={Version} layer=memory", requestedCourseId, meta.RootCourseId, meta.Version);
            return memorySnapshot;
        }

        var cached = await ReadCompressedAsync<CourseMapSnapshot>(key, ct);
        if (cached is not null)
        {
            _memory.Set(key, cached, TimeSpan.FromSeconds(20));
            _logger.LogInformation("TFDBG MAP SNAPSHOT HIT requested={RequestedCourseId} root={RootCourseId} version={Version} layer=redis", requestedCourseId, meta.RootCourseId, meta.Version);
            return cached;
        }

        var candidate = new Lazy<Task<CourseMapSnapshot?>>(
            () => BuildAndCacheSnapshotAsync(meta, key),
            LazyThreadSafetyMode.ExecutionAndPublication);
        var lazy = SnapshotBuilds.GetOrAdd(key, candidate);
        _logger.LogInformation(
            "TFDBG MAP SNAPSHOT {Action} requested={RequestedCourseId} root={RootCourseId} version={Version}",
            ReferenceEquals(lazy, candidate) ? "BUILD" : "JOIN",
            requestedCourseId,
            meta.RootCourseId,
            meta.Version);
        var buildTask = lazy.Value;
        _ = buildTask.ContinueWith(
            completedTask =>
            {
                if (SnapshotBuilds.TryGetValue(key, out var current) && ReferenceEquals(current, lazy))
                    SnapshotBuilds.TryRemove(key, out _);
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        return await buildTask.WaitAsync(ct);
    }

    private async Task<CourseMapSnapshot?> BuildAndCacheSnapshotAsync(CourseMapMetaInternalResponse meta, string key)
    {
        var snapshot = await BuildSnapshotAsync(meta, CancellationToken.None);
        if (snapshot is null) return null;
        _memory.Set(key, snapshot, TimeSpan.FromSeconds(20));
        await WriteCompressedAsync(
            key,
            snapshot,
            TaskForgeCache.Ttl(_cfg, "CourseMapSnapshot", 60),
            CancellationToken.None);
        return snapshot;
    }

    private async Task<CourseMapSnapshot?> BuildSnapshotAsync(CourseMapMetaInternalResponse meta, CancellationToken ct)
    {
        var startedAt = DateTimeOffset.UtcNow;
        _logger.LogInformation("TFDBG MAP SNAPSHOT BUILD START root={RootCourseId} version={Version}", meta.RootCourseId, meta.Version);
        var mapTask = GetInternalAsync<CourseMapInternalResponse>(
            _clients,
            _cfg,
            ServiceUrl(_cfg, "EducationApi", "http://education-api:8080"),
            $"/api/internal/courses/{meta.RootCourseId:D}/map",
            ct);
        var treeTask = GetInternalAsync<CourseTreeResponse>(
            _clients,
            _cfg,
            ServiceUrl(_cfg, "EducationApi", "http://education-api:8080"),
            $"/api/internal/courses/{meta.RootCourseId:D}/tree",
            ct);

        await Task.WhenAll(mapTask, treeTask);
        var map = await mapTask;
        var tree = await treeTask;
        if (map is null || tree is null || tree.CourseIds.Length == 0) return null;

        var courseIds = tree.CourseIds.Where(x => x != Guid.Empty).Distinct().Take(5000).ToArray();
        using var scope = _scopeFactory.CreateScope();
        var snapshotDb = scope.ServiceProvider.GetRequiredService<TasksDbContext>();
        var assignments = await snapshotDb.Assignments.AsNoTracking()
            .Where(x => courseIds.Contains(x.CourseId))
            .Select(x => new CourseMapProgressionService.AssignmentProgressionRow(
                x.Id,
                x.CourseId,
                x.Title,
                x.Type,
                x.Language,
                x.Tags,
                x.Description,
                x.IsVisible,
                x.Sort))
            .ToListAsync(ct);
        assignments = assignments
            .Select(x => x with { Description = TrimPreview(x.Description) })
            .ToList();
        foreach (var course in tree.Courses)
            course.Description = TrimPreview(course.Description);

        var snapshot = new CourseMapSnapshot(
            map.RootCourseId,
            map.Version,
            map.Document,
            map.UpdatedAt,
            map.UpdatedBy,
            tree,
            assignments);
        _logger.LogInformation(
            "TFDBG MAP SNAPSHOT BUILD END root={RootCourseId} version={Version} courses={Courses} assignments={Assignments} durationMs={DurationMs:F2}",
            map.RootCourseId,
            map.Version,
            tree.CourseIds.Length,
            assignments.Count,
            (DateTimeOffset.UtcNow - startedAt).TotalMilliseconds);
        return snapshot;
    }

    private async Task<CourseMapMetaInternalResponse?> GetMapMetaAsync(Guid courseId, CancellationToken ct, bool forceFresh = false)
    {
        var key = $"course-map-meta:{courseId:N}";
        if (!forceFresh && _memory.TryGetValue<CourseMapMetaInternalResponse>(key, out var cached) && cached is not null) return cached;
        var meta = await GetInternalAsync<CourseMapMetaInternalResponse>(
            _clients,
            _cfg,
            ServiceUrl(_cfg, "EducationApi", "http://education-api:8080"),
            $"/api/internal/courses/{courseId:D}/map/meta",
            ct);
        if (meta is not null) _memory.Set(key, meta, TimeSpan.FromSeconds(5));
        return meta;
    }

    private IEnumerable<SegmentPayload> BuildSegments(
        CourseMapSnapshot snapshot,
        CourseMapProgressionService.Evaluation evaluation,
        Guid requestedCourseId)
    {
        var document = evaluation.LearningDocument;
        if (document is null || document.Value.ValueKind != JsonValueKind.Object) yield break;

        var nodes = ReadArray(document, "nodes");
        var edges = ReadArray(document, "edges");
        var nodeById = nodes
            .Select(x => (Id: GetId(x), Node: x))
            .Where(x => !string.IsNullOrWhiteSpace(x.Id))
            .ToDictionary(x => x.Id!, x => x.Node, StringComparer.Ordinal);
        var assignmentById = snapshot.Assignments.ToDictionary(x => x.Id);
        var courseById = snapshot.Tree.Courses.ToDictionary(x => x.Id);
        var subtree = evaluation.RequestedSubtreeCourseIds;

        Guid? NodeSegment(JsonElement node)
        {
            var entityId = ReadEntityId(node);
            var type = GetString(node, "type");
            if (string.Equals(type, "course", StringComparison.OrdinalIgnoreCase) && entityId.HasValue)
            {
                if (entityId.Value == requestedCourseId) return requestedCourseId;
                if (courseById.TryGetValue(entityId.Value, out var course)
                    && course.ParentCourseId.HasValue
                    && subtree.Contains(course.ParentCourseId.Value))
                {
                    return course.ParentCourseId.Value;
                }
                return entityId.Value;
            }
            if (entityId.HasValue && assignmentById.TryGetValue(entityId.Value, out var assignment))
                return assignment.CourseId;
            return null;
        }

        var nodeSegment = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            var id = GetId(node);
            if (string.IsNullOrWhiteSpace(id)) continue;
            var segment = NodeSegment(node);
            if (segment.HasValue && subtree.Contains(segment.Value)) nodeSegment[id] = segment.Value;
        }

        var incomingSourcesByTarget = edges
            .Select(edge => (Target: GetString(edge, "target"), Source: GetString(edge, "source")))
            .Where(x => !string.IsNullOrWhiteSpace(x.Target) && !string.IsNullOrWhiteSpace(x.Source))
            .GroupBy(x => x.Target!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Source!).ToArray(), StringComparer.Ordinal);

        foreach (var locked in nodes.Where(x => string.Equals(GetString(x, "type"), "locked", StringComparison.OrdinalIgnoreCase)))
        {
            var id = GetId(locked);
            if (string.IsNullOrWhiteSpace(id) || nodeSegment.ContainsKey(id)) continue;
            if (!incomingSourcesByTarget.TryGetValue(id, out var incomingSources)) continue;
            var incomingSource = incomingSources.FirstOrDefault(nodeSegment.ContainsKey);
            if (!string.IsNullOrWhiteSpace(incomingSource)) nodeSegment[id] = nodeSegment[incomingSource!];
        }

        Guid? EdgeSegment(JsonElement edge)
        {
            var sourceId = GetString(edge, "source");
            if (string.IsNullOrWhiteSpace(sourceId) || !nodeById.TryGetValue(sourceId, out var sourceNode)) return null;
            var sourceType = GetString(sourceNode, "type");
            var sourceEntityId = ReadEntityId(sourceNode);
            if (string.Equals(sourceType, "course", StringComparison.OrdinalIgnoreCase) && sourceEntityId.HasValue)
                return sourceEntityId.Value;
            return nodeSegment.TryGetValue(sourceId, out var segment) ? segment : null;
        }

        var nodesBySegment = nodes
            .Select(node => (Node: node, Id: GetId(node)))
            .Where(x => !string.IsNullOrWhiteSpace(x.Id) && nodeSegment.ContainsKey(x.Id!))
            .GroupBy(x => nodeSegment[x.Id!])
            .ToDictionary(g => g.Key, g => g.Select(x => x.Node).ToArray());
        var edgesBySegment = edges
            .Select(edge => (Edge: edge, Segment: EdgeSegment(edge)))
            .Where(x => x.Segment.HasValue)
            .GroupBy(x => x.Segment!.Value)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Edge).ToArray());

        var progress = ReadCourseProgress(document);
        var visibleCourses = evaluation.VisibleCourseIds.Intersect(subtree).ToHashSet();
        var order = BuildCourseSegmentOrder(requestedCourseId, snapshot.Tree.Courses, visibleCourses);
        var solved = evaluation.SolvedAssignmentIds;

        foreach (var courseId in order)
        {
            var courseNodes = nodesBySegment.GetValueOrDefault(courseId) ?? Array.Empty<JsonElement>();
            var courseEdges = edgesBySegment.GetValueOrDefault(courseId) ?? Array.Empty<JsonElement>();
            if (courseNodes.Length == 0 && courseEdges.Length == 0) continue;

            var chunkCount = System.Math.Max(
                (courseNodes.Length + SegmentNodeChunkSize - 1) / SegmentNodeChunkSize,
                (courseEdges.Length + SegmentEdgeChunkSize - 1) / SegmentEdgeChunkSize);
            chunkCount = System.Math.Max(1, chunkCount);

            for (var chunkIndex = 0; chunkIndex < chunkCount; chunkIndex++)
            {
                var segmentNodes = courseNodes
                    .Skip(chunkIndex * SegmentNodeChunkSize)
                    .Take(SegmentNodeChunkSize)
                    .ToArray();
                var segmentEdges = courseEdges
                    .Skip(chunkIndex * SegmentEdgeChunkSize)
                    .Take(SegmentEdgeChunkSize)
                    .ToArray();
                var entityIds = segmentNodes
                    .Select(ReadEntityId)
                    .Where(x => x.HasValue)
                    .Select(x => x!.Value)
                    .ToHashSet();
                var segmentProgress = new Dictionary<string, CourseProgressCard>(StringComparer.Ordinal);
                foreach (var node in segmentNodes)
                {
                    var id = GetId(node);
                    if (id is not null && progress.TryGetValue(id, out var value)) segmentProgress[id] = value;
                }

                var segmentAssignments = entityIds.Where(assignmentById.ContainsKey).Select(id => CreateAssignmentCard(assignmentById[id], solved)).ToArray();
                var segmentCourses = entityIds.Where(courseById.ContainsKey).Select(id => CreateCourseCard(courseById[id])).ToArray();
                _logger.LogInformation(
                    "TFDBG MAP SEGMENT root={RootCourseId} requested={RequestedCourseId} course={CourseId} chunk={Chunk}/{ChunkCount} nodes={Nodes} edges={Edges} assignments={Assignments} courses={Courses}",
                    snapshot.RootCourseId,
                    requestedCourseId,
                    courseId,
                    chunkIndex + 1,
                    chunkCount,
                    segmentNodes.Length,
                    segmentEdges.Length,
                    segmentAssignments.Length,
                    segmentCourses.Length);
                yield return new SegmentPayload(
                    courseId,
                    segmentNodes,
                    segmentEdges,
                    segmentAssignments,
                    segmentCourses,
                    segmentProgress);
            }
        }
    }

    private static List<Guid> BuildCourseSegmentOrder(
        Guid requestedCourseId,
        IReadOnlyCollection<CourseTreeCourseDto> courses,
        HashSet<Guid> visibleCourseIds)
    {
        var children = courses
            .Where(x => x.ParentCourseId.HasValue)
            .GroupBy(x => x.ParentCourseId!.Value)
            .ToDictionary(
                g => g.Key,
                g => g.Where(x => visibleCourseIds.Contains(x.Id)).OrderBy(x => x.Sort).ThenBy(x => x.Title).Select(x => x.Id).ToArray());
        var result = new List<Guid>();
        var queue = new Queue<Guid>();
        if (visibleCourseIds.Contains(requestedCourseId)) queue.Enqueue(requestedCourseId);
        var seen = new HashSet<Guid>();
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            if (!seen.Add(id)) continue;
            result.Add(id);
            if (!children.TryGetValue(id, out var childIds)) continue;
            foreach (var childId in childIds) queue.Enqueue(childId);
        }
        return result;
    }

    private static AssignmentCard[] BuildAssignmentCards(
        CourseMapSnapshot snapshot,
        HashSet<Guid> solvedIds,
        HashSet<Guid> entityIds)
        => snapshot.Assignments
            .Where(x => entityIds.Contains(x.Id))
            .Select(x => CreateAssignmentCard(x, solvedIds))
            .ToArray();

    private static AssignmentCard CreateAssignmentCard(
        CourseMapProgressionService.AssignmentProgressionRow assignment,
        HashSet<Guid> solvedIds)
        => new(
            assignment.Id,
            assignment.CourseId,
            assignment.Title,
            assignment.Type,
            assignment.Language,
            assignment.Tags,
            TrimPreview(assignment.Description),
            assignment.IsVisible,
            assignment.Sort,
            solvedIds.Contains(assignment.Id));

    private static CourseCard[] BuildCourseCards(CourseMapSnapshot snapshot, HashSet<Guid> entityIds)
        => snapshot.Tree.Courses
            .Where(x => entityIds.Contains(x.Id))
            .Select(CreateCourseCard)
            .ToArray();

    private static CourseCard CreateCourseCard(CourseTreeCourseDto course)
        => new(
            course.Id,
            course.ParentCourseId,
            course.Title,
            TrimPreview(course.Description),
            course.IsPublic,
            course.IsHiddenFromStudents,
            course.Sort);

    private static string? TrimPreview(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return value;
        var normalized = value.Trim();
        return normalized.Length <= 420 ? normalized : normalized[..420];
    }

    private static JsonElement BuildFallbackDocument(
        CourseMapSnapshot snapshot,
        Guid requestedCourseId,
        HashSet<Guid> accessibleCourseIds,
        List<CourseMapProgressionService.AssignmentProgressionRow> assignments,
        HashSet<Guid> solvedIds)
    {
        var subtree = BuildSubtreeCourseIds(requestedCourseId, snapshot.Tree.Courses);
        subtree.IntersectWith(accessibleCourseIds);
        var nodes = new List<object>();
        var edges = new List<object>();
        var courseByParent = snapshot.Tree.Courses
            .Where(x => x.ParentCourseId.HasValue && subtree.Contains(x.Id))
            .GroupBy(x => x.ParentCourseId!.Value)
            .ToDictionary(g => g.Key, g => g.OrderBy(x => x.Sort).ThenBy(x => x.Title).ToList());
        var assignmentByCourse = assignments
            .Where(x => subtree.Contains(x.CourseId))
            .GroupBy(x => x.CourseId)
            .ToDictionary(g => g.Key, g => g.OrderBy(x => x.Sort).ThenBy(x => x.Title).ToList());
        var courseRows = snapshot.Tree.Courses.Where(x => subtree.Contains(x.Id)).ToDictionary(x => x.Id);
        var queue = new Queue<(Guid CourseId, int Depth)>();
        queue.Enqueue((requestedCourseId, 0));
        var seen = new HashSet<Guid>();
        while (queue.Count > 0)
        {
            var (courseId, depth) = queue.Dequeue();
            if (!seen.Add(courseId) || !courseRows.TryGetValue(courseId, out var course)) continue;
            var courseNodeId = $"course:{courseId:D}";
            nodes.Add(new
            {
                id = courseNodeId,
                type = "course",
                entityId = courseId.ToString("D"),
                position = new { x = depth * 360d, y = course.Sort * 180d },
                settings = new { }
            });

            var y = 0;
            foreach (var assignment in assignmentByCourse.GetValueOrDefault(courseId) ?? new List<CourseMapProgressionService.AssignmentProgressionRow>())
            {
                var assignmentNodeId = $"assignment:{assignment.Id:D}";
                nodes.Add(new
                {
                    id = assignmentNodeId,
                    type = assignment.Type,
                    entityId = assignment.Id.ToString("D"),
                    position = new { x = depth * 360d + 320d, y = y * 150d },
                    settings = new { }
                });
                edges.Add(new
                {
                    id = $"fallback:{courseId:D}:{assignment.Id:D}",
                    source = courseNodeId,
                    sourceHandle = "out",
                    target = assignmentNodeId,
                    targetHandle = "in",
                    settings = new { }
                });
                y++;
            }

            foreach (var child in courseByParent.GetValueOrDefault(courseId) ?? new List<CourseTreeCourseDto>())
            {
                var childNodeId = $"course:{child.Id:D}";
                edges.Add(new
                {
                    id = $"fallback-course:{courseId:D}:{child.Id:D}",
                    source = courseNodeId,
                    sourceHandle = "out",
                    target = childNodeId,
                    targetHandle = "in",
                    settings = new { }
                });
                queue.Enqueue((child.Id, depth + 1));
            }
        }

        return JsonSerializer.SerializeToElement(new
        {
            schemaVersion = 1,
            viewport = new { x = 0, y = 0, zoom = 1 },
            nodes,
            edges
        });
    }

    private static HashSet<Guid> BuildSubtreeCourseIds(Guid rootCourseId, IReadOnlyCollection<CourseTreeCourseDto> courses)
    {
        var children = courses
            .Where(x => x.ParentCourseId.HasValue)
            .GroupBy(x => x.ParentCourseId!.Value)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Id).ToArray());
        var result = new HashSet<Guid>();
        var stack = new Stack<Guid>();
        stack.Push(rootCourseId);
        while (stack.Count > 0)
        {
            var id = stack.Pop();
            if (!result.Add(id)) continue;
            if (!children.TryGetValue(id, out var childIds)) continue;
            foreach (var childId in childIds) stack.Push(childId);
        }
        return result;
    }

    private ProjectionState BuildProjectionState(
        CourseMapSnapshot snapshot,
        CourseMapProgressionService.Evaluation evaluation,
        Guid userId,
        bool bypassStudentVisibility)
    {
        var visibleNodeIds = ReadArray(evaluation.LearningDocument, "nodes")
            .Select(GetId)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Cast<string>()
            .ToArray();
        var visibleEdgeIds = ReadArray(evaluation.LearningDocument, "edges")
            .Select(GetId)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Cast<string>()
            .ToArray();

        var accessibleCourseIds = snapshot.Tree.CourseIds
            .Where(x => evaluation.VisibleCourseIds.Contains(x) || evaluation.RequestedSubtreeCourseIds.Contains(x))
            .Distinct()
            .ToArray();

        return new ProjectionState(
            string.Empty,
            userId,
            snapshot.RootCourseId,
            evaluation.RequestedCourseId,
            snapshot.Version,
            0,
            bypassStudentVisibility,
            accessibleCourseIds,
            evaluation.VisibleCourseIds.ToArray(),
            evaluation.VisibleAssignmentIds.ToArray(),
            evaluation.SolvedAssignmentIds.ToArray(),
            visibleNodeIds,
            visibleEdgeIds,
            ReadCourseProgress(evaluation.LearningDocument),
            DateTimeOffset.UtcNow);
    }

    private static Dictionary<string, CourseProgressCard> ReadCourseProgress(JsonElement? document)
    {
        var result = new Dictionary<string, CourseProgressCard>(StringComparer.Ordinal);
        if (document is null || document.Value.ValueKind != JsonValueKind.Object) return result;
        if (!document.Value.TryGetProperty("courseProgress", out var progress) || progress.ValueKind != JsonValueKind.Object) return result;
        foreach (var property in progress.EnumerateObject())
        {
            var value = property.Value;
            var total = value.TryGetProperty("total", out var totalEl) && totalEl.TryGetInt32(out var totalValue) ? totalValue : 0;
            var solved = value.TryGetProperty("solved", out var solvedEl) && solvedEl.TryGetInt32(out var solvedValue) ? solvedValue : 0;
            var percent = value.TryGetProperty("percent", out var percentEl) && percentEl.TryGetInt32(out var percentValue) ? percentValue : 0;
            result[property.Name] = new CourseProgressCard(total, solved, percent);
        }
        return result;
    }

    private static JsonElement ReadViewport(JsonElement? document)
    {
        if (document is not null
            && document.Value.ValueKind == JsonValueKind.Object
            && document.Value.TryGetProperty("viewport", out var viewport)
            && viewport.ValueKind == JsonValueKind.Object)
        {
            return viewport.Clone();
        }
        return JsonSerializer.SerializeToElement(new { x = 0, y = 0, zoom = 1 });
    }

    private static JsonElement[] ReadArray(JsonElement? document, string propertyName)
    {
        if (document is null || document.Value.ValueKind != JsonValueKind.Object) return Array.Empty<JsonElement>();
        if (!document.Value.TryGetProperty(propertyName, out var element) || element.ValueKind != JsonValueKind.Array) return Array.Empty<JsonElement>();
        return element.EnumerateArray().Select(x => x.Clone()).ToArray();
    }

    private static string? GetId(JsonElement element) => GetString(element, "id");

    private static string? GetString(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.String)
        {
            return null;
        }
        return property.GetString()?.Trim();
    }

    private static Guid? ReadEntityId(JsonElement element)
    {
        var raw = GetString(element, "entityId");
        return Guid.TryParse(raw, out var parsed) ? parsed : null;
    }

    private DeltaResult ResetDelta(int version = 0)
        => new(
            true,
            null,
            0,
            version,
            Array.Empty<JsonElement>(),
            Array.Empty<string>(),
            Array.Empty<JsonElement>(),
            Array.Empty<string>(),
            Array.Empty<AssignmentCard>(),
            Array.Empty<CourseCard>(),
            new Dictionary<string, CourseProgressCard>(),
            Array.Empty<Guid>());

    private string SnapshotKey(Guid rootCourseId, int version)
        => TaskForgeCache.Key("tasks:course-map-snapshot:v1", rootCourseId, version);

    private string ProjectionKey(string token)
        => TaskForgeCache.Key("tasks:course-map-projection:v1", token);

    private string CurrentProjectionPointerKey(Guid userId, Guid rootCourseId, Guid requestedCourseId, int version, bool bypass)
        => TaskForgeCache.Key("tasks:course-map-projection-current:v2", userId, rootCourseId, requestedCourseId, version, bypass);

    private async Task SaveProjectionStateAsync(ProjectionState state, CancellationToken ct)
    {
        var key = ProjectionKey(state.ProjectionToken);
        _memory.Set(key, state, TimeSpan.FromSeconds(30));
        await WriteCompressedAsync(
            key,
            state,
            TaskForgeCache.Ttl(_cfg, "CourseMapProjection", 1800),
            ct);
    }

    private async Task SaveCurrentProjectionPointerAsync(ProjectionState state, CancellationToken ct)
    {
        var key = CurrentProjectionPointerKey(
            state.UserId,
            state.RootCourseId,
            state.RequestedCourseId,
            state.Version,
            state.BypassStudentVisibility);
        try
        {
            var tokenBytes = Encoding.UTF8.GetBytes(state.ProjectionToken);
            _memory.Set(key, tokenBytes, TimeSpan.FromSeconds(30));
            await _cache.SetAsync(
                key,
                tokenBytes,
                new DistributedCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = TaskForgeCache.Ttl(_cfg, "CourseMapProjection", 1800)
                },
                ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Course-map projection pointer write failed for root {RootCourseId}", state.RootCourseId);
        }
    }

    private async Task<ProjectionState?> LoadProjectionStateAsync(string token, CancellationToken ct)
    {
        var key = ProjectionKey(token);
        if (_memory.TryGetValue<ProjectionState>(key, out var cached) && cached is not null) return cached;
        var state = await ReadCompressedAsync<ProjectionState>(key, ct);
        if (state is not null) _memory.Set(key, state, TimeSpan.FromSeconds(30));
        return state;
    }

    private async Task<T?> ReadCompressedAsync<T>(string key, CancellationToken ct)
    {
        try
        {
            var payload = await _cache.GetAsync(key, ct);
            if (payload is null || payload.Length == 0) return default;
            await using var input = new MemoryStream(payload, writable: false);
            await using var gzip = new GZipStream(input, CompressionMode.Decompress, leaveOpen: false);
            return await JsonSerializer.DeserializeAsync<T>(gzip, JsonOptions, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Course-map cache read failed for {CacheKey}", key);
            return default;
        }
    }

    private async Task WriteCompressedAsync<T>(string key, T value, TimeSpan ttl, CancellationToken ct)
    {
        try
        {
            await using var output = new MemoryStream();
            await using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
            {
                await JsonSerializer.SerializeAsync(gzip, value, JsonOptions, ct);
            }
            await _cache.SetAsync(
                key,
                output.ToArray(),
                new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = ttl },
                ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Course-map cache write failed for {CacheKey}", key);
        }
    }

    private async Task<byte[]?> SafeGetAsync(string key, CancellationToken ct)
    {
        // These keys are mutable "current projection" pointers shared by every
        // assignment-api instance. Reading the per-process memory cache first can
        // hide a pointer written by another node for up to 30 seconds. Prefer the
        // distributed cache and use memory only as a degraded fallback.
        try
        {
            var value = await _cache.GetAsync(key, ct);
            if (value is not null)
            {
                _memory.Set(key, value, TimeSpan.FromSeconds(30));
                return value;
            }
            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Course-map cache pointer read failed for {CacheKey}", key);
            return _memory.TryGetValue<byte[]>(key, out var cached) ? cached : null;
        }
    }

    private sealed record CourseMapSnapshot(
        Guid RootCourseId,
        int Version,
        JsonElement? Document,
        DateTimeOffset? UpdatedAt,
        Guid? UpdatedBy,
        CourseTreeResponse Tree,
        List<CourseMapProgressionService.AssignmentProgressionRow> Assignments);

    private sealed record ProjectionState(
        string ProjectionToken,
        Guid UserId,
        Guid RootCourseId,
        Guid RequestedCourseId,
        int Version,
        long ProjectionRevision,
        bool BypassStudentVisibility,
        Guid[] AccessibleCourseIds,
        Guid[] VisibleCourseIds,
        Guid[] VisibleAssignmentIds,
        Guid[] SolvedAssignmentIds,
        string[] VisibleNodeIds,
        string[] VisibleEdgeIds,
        Dictionary<string, CourseProgressCard> CourseProgress,
        DateTimeOffset VerifiedAt);
}
