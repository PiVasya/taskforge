using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using TaskForge.Tasks.Api.Data;
using TaskForge.Tasks.Api.Domain;
using TaskForge.Tasks.Api.Services.Access;
using TaskForge.Tasks.Api.Services.Sql;
using TaskForge.Tasks.Api.Domain.Sql;

using TaskForge.Tasks.Api.Contracts;
using static TaskForge.Tasks.Api.Services.Access.AssignmentApiAccessService;
using static TaskForge.Tasks.Api.Services.Common.AssignmentApiCommonService;
using static TaskForge.Tasks.Api.Services.Image.AssignmentApiImageService;
using static TaskForge.Tasks.Api.Services.Mapping.AssignmentApiMappingService;
using static TaskForge.Tasks.Api.Services.Math.AssignmentApiMathService;
using static TaskForge.Tasks.Api.Services.Results.AssignmentApiResultsService;
using static TaskForge.Tasks.Api.Services.Serialization.AssignmentApiSerializationService;
using static TaskForge.Tasks.Api.Services.Serialization.AssignmentTaskGraphJsonService;
using static TaskForge.Tasks.Api.Services.Testing.AssignmentApiTestingService;

namespace TaskForge.Tasks.Api.Endpoints;

internal static partial class AssignmentApiEndpoints
{
    private static GraphImportOptions ReadGraphImportOptions(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty("options", out var options)
            || options.ValueKind != JsonValueKind.Object) return new GraphImportOptions();
        return new GraphImportOptions(
            ReadOption(options, "updateContent", true),
            ReadOption(options, "updateChecks", true),
            ReadOption(options, "updateVisibility", true),
            ReadOption(options, "updateConnections", true),
            ReadOption(options, "updateConnectionAccess", true),
            ReadOption(options, "updateLayout", true));
    }

    private static JsonElement ReadGraphImportPayload(JsonElement payload)
    {
        if (payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty("graph", out var graph)
            && graph.ValueKind is JsonValueKind.Object or JsonValueKind.Array) return graph.Clone();
        return payload;
    }

    private static bool ReadOption(JsonElement owner, string name, bool fallback)
    {
        if (!owner.TryGetProperty(name, out var value)) return fallback;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => fallback
        };
    }


    private static bool ReadFreshMapRequest(HttpContext http)
    {
        var raw = http.Request.Query["fresh"].ToString().Trim();
        if (raw.Length == 0) return false;
        return raw.Equals("1", StringComparison.OrdinalIgnoreCase)
            || raw.Equals("true", StringComparison.OrdinalIgnoreCase)
            || raw.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || raw.Equals("on", StringComparison.OrdinalIgnoreCase);
    }

    private static AssignmentRequest FilterExistingImportRequest(AssignmentRequest request, GraphImportOptions options)
    {
        return request with
        {
            Title = options.UpdateContent ? request.Title : null,
            Description = options.UpdateContent ? request.Description : null,
            Type = options.UpdateContent ? request.Type : null,
            Language = options.UpdateContent ? request.Language : null,
            AllowedLanguages = options.UpdateContent ? request.AllowedLanguages : null,
            Tags = options.UpdateContent ? request.Tags : null,
            Difficulty = options.UpdateContent ? request.Difficulty : null,
            Rating = options.UpdateContent ? request.Rating : null,
            StarterCode = options.UpdateContent ? request.StarterCode : null,
            TestsJson = options.UpdateChecks ? request.TestsJson : null,
            Tests = options.UpdateChecks ? request.Tests : null,
            TestCases = options.UpdateChecks ? request.TestCases : null,
            CodeForbiddenCalls = options.UpdateChecks ? request.CodeForbiddenCalls : null,
            CodeRequiredCalls = options.UpdateChecks ? request.CodeRequiredCalls : null,
            ImageTestReferenceKey = options.UpdateChecks ? request.ImageTestReferenceKey : null,
            ImageTestSimilarityThreshold = options.UpdateChecks ? request.ImageTestSimilarityThreshold : null,
            IsVisible = options.UpdateVisibility ? request.IsVisible : null,
            IsHidden = options.UpdateVisibility ? request.IsHidden : null,
            Sort = null,
            AnalyticsSettings = null
        };
    }

    private static WebApplication MapAssignmentsEndpoints(WebApplication app)
    {
        app.MapGet("/api/courses/{courseId:guid}/assignments", async (Guid courseId, HttpContext http, IConfiguration cfg, TasksDbContext db, IHttpClientFactory clients, CancellationToken ct) =>
        {
            var includeHidden = IsEditor(http, cfg);
            var userId = TaskForgeRequestSecurity.UserId(http, cfg);
            if (!includeHidden)
            {
                http.Response.Headers.CacheControl = "no-store";
                if (!userId.HasValue) return Microsoft.AspNetCore.Http.Results.Unauthorized();
                var evaluation = await CourseMapProgressionService.LoadEvaluationAsync(courseId, userId.Value, db, clients, cfg, ct);
                if (evaluation == null)
                    return Microsoft.AspNetCore.Http.Results.NotFound(new { message = "Курс не найден или ещё не открыт по прогрессии.", code = "COURSE_NOT_AVAILABLE" });

                var visibleIds = evaluation.VisibleAssignmentIds;
                var rows = await db.Assignments.AsNoTracking()
                    .Where(x => x.CourseId == courseId && visibleIds.Contains(x.Id))
                    .OrderBy(x => x.Sort)
                    .ThenBy(x => x.CreatedAt)
                    .ToListAsync(ct);
                return Microsoft.AspNetCore.Http.Results.Ok(rows.Select(x => ToDto(x, false, evaluation.SolvedAssignmentIds.Contains(x.Id))).ToList());
            }

            var editorRows = await db.Assignments.AsNoTracking()
                .Where(x => x.CourseId == courseId)
                .OrderBy(x => x.Sort)
                .ThenBy(x => x.CreatedAt)
                .ToListAsync(ct);
            var editorSolvedIds = userId.HasValue
                ? await LoadSolvedAssignmentIdsAsync(userId.Value, editorRows.Select(x => x.Id), db, clients, cfg, ct)
                : new HashSet<Guid>();
            return Microsoft.AspNetCore.Http.Results.Ok(editorRows.Select(x => ToDto(x, true, editorSolvedIds.Contains(x.Id))).ToList());
        });


        app.MapGet("/api/courses/{courseId:guid}/learning-map", async (Guid courseId, HttpContext http, IConfiguration cfg, TasksDbContext db, IHttpClientFactory clients, CancellationToken ct) =>
        {
            var userId = TaskForgeRequestSecurity.UserId(http, cfg);
            if (!userId.HasValue) return Microsoft.AspNetCore.Http.Results.Unauthorized();
            http.Response.Headers.CacheControl = "no-store";
            TaskForgeDebugTrace.Map("HTTP_LEARNING_MAP_BEGIN",
                ("user", userId.Value),
                ("requestedCourse", courseId),
                ("editorBypass", IsEditor(http, cfg)),
                ("referer", http.Request.Headers["Referer"].FirstOrDefault()),
                ("userAgent", http.Request.Headers["User-Agent"].FirstOrDefault()));

            var evaluation = await CourseMapProgressionService.LoadEvaluationAsync(
                courseId,
                userId.Value,
                db,
                clients,
                cfg,
                ct,
                IsEditor(http, cfg));
            if (evaluation == null)
            {
                TaskForgeDebugTrace.Map("HTTP_LEARNING_MAP_END", ("user", userId.Value), ("requestedCourse", courseId), ("status", 404));
                return Microsoft.AspNetCore.Http.Results.NotFound(new { message = "Курс не найден или ещё не открыт.", code = "COURSE_NOT_AVAILABLE" });
            }

            TaskForgeDebugTrace.Map("HTTP_LEARNING_MAP_END",
                ("user", userId.Value),
                ("requestedCourse", courseId),
                ("rootCourse", evaluation.RootCourseId),
                ("version", evaluation.Version),
                ("visibleAssignmentIds", TaskForgeDebugTrace.MapList(evaluation.VisibleAssignmentIds)),
                ("solvedAssignmentIds", TaskForgeDebugTrace.MapList(evaluation.SolvedAssignmentIds)),
                ("status", 200));
            return Microsoft.AspNetCore.Http.Results.Ok(new
            {
                rootCourseId = evaluation.RootCourseId,
                requestedCourseId = evaluation.RequestedCourseId,
                version = evaluation.Version,
                document = evaluation.LearningDocument,
                updatedAt = evaluation.UpdatedAt,
                updatedBy = evaluation.UpdatedBy
            });
        });

        app.MapGet("/api/courses/{courseId:guid}/learning-map/stream", async (
            Guid courseId,
            HttpContext http,
            IConfiguration cfg,
            CourseMapProjectionService projection,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("TaskForge.CourseMapStream");
            var startedAt = DateTimeOffset.UtcNow;
            var userId = TaskForgeRequestSecurity.UserId(http, cfg);
            if (!userId.HasValue)
            {
                http.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            var bypass = IsEditor(http, cfg);
            var fresh = ReadFreshMapRequest(http);
            logger.LogInformation("TFDBG MAP HTTP STREAM START requested={RequestedCourseId} user={UserId} bypass={Bypass} fresh={Fresh}", courseId, userId.Value, bypass, fresh);
            TaskForgeDebugTrace.Map("HTTP_STREAM_BEGIN",
                ("user", userId.Value),
                ("requestedCourse", courseId),
                ("bypassStudentVisibility", bypass),
                ("fresh", fresh),
                ("referer", http.Request.Headers["Referer"].FirstOrDefault()),
                ("userAgent", http.Request.Headers["User-Agent"].FirstOrDefault()));
            var session = await projection.CreateSessionAsync(courseId, userId.Value, bypass, fresh, ct);
            if (session is null)
            {
                TaskForgeDebugTrace.Map("HTTP_STREAM_END", ("user", userId.Value), ("requestedCourse", courseId), ("status", 404), ("reason", "session-miss"));
                http.Response.StatusCode = StatusCodes.Status404NotFound;
                http.Response.ContentType = "application/json; charset=utf-8";
                await http.Response.WriteAsJsonAsync(new { message = "Курс не найден или ещё не открыт.", code = "COURSE_NOT_AVAILABLE" }, cancellationToken: ct);
                return;
            }

            http.Response.StatusCode = StatusCodes.Status200OK;
            http.Response.ContentType = "application/x-ndjson; charset=utf-8";
            http.Response.Headers.CacheControl = "no-store";
            http.Response.Headers["X-Accel-Buffering"] = "no";
            var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

            TaskForgeDebugTrace.Map("HTTP_STREAM_META",
                ("user", userId.Value),
                ("requestedCourse", courseId),
                ("rootCourse", session.Meta.RootCourseId),
                ("version", session.Meta.Version),
                ("projectionToken", session.Meta.ProjectionToken),
                ("revision", session.Meta.ProjectionRevision),
                ("visibleNodeCount", session.Meta.VisibleNodeCount),
                ("visibleEdgeCount", session.Meta.VisibleEdgeCount));
            await http.Response.WriteAsync(JsonSerializer.Serialize(new { type = "meta", data = session.Meta }, jsonOptions) + "\n", ct);
            await http.Response.Body.FlushAsync(ct);
            logger.LogInformation(
                "TFDBG MAP HTTP STREAM META requested={RequestedCourseId} user={UserId} version={Version} visibleNodes={VisibleNodes} visibleEdges={VisibleEdges} durationMs={DurationMs:F2}",
                courseId,
                userId.Value,
                session.Meta.Version,
                session.Meta.VisibleNodeCount,
                session.Meta.VisibleEdgeCount,
                (DateTimeOffset.UtcNow - startedAt).TotalMilliseconds);

            var segmentIndex = 0;
            foreach (var segment in session.Segments)
            {
                segmentIndex++;
                TaskForgeDebugTrace.Map("HTTP_STREAM_SEGMENT",
                    ("user", userId.Value),
                    ("requestedCourse", courseId),
                    ("segmentIndex", segmentIndex),
                    ("segmentCourse", segment.CourseId),
                    ("nodeIds", TaskForgeDebugTrace.MapList(segment.Nodes.Select(x => x.ValueKind == JsonValueKind.Object && x.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null))),
                    ("edgeIds", TaskForgeDebugTrace.MapList(segment.Edges.Select(x => x.ValueKind == JsonValueKind.Object && x.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null))),
                    ("assignmentIds", TaskForgeDebugTrace.MapList(segment.Assignments.Select(x => x.Id))),
                    ("courseIds", TaskForgeDebugTrace.MapList(segment.Courses.Select(x => x.Id))));
                await http.Response.WriteAsync(JsonSerializer.Serialize(new { type = "segment", data = segment }, jsonOptions) + "\n", ct);
                await http.Response.Body.FlushAsync(ct);
                logger.LogInformation(
                    "TFDBG MAP HTTP STREAM FLUSH requested={RequestedCourseId} user={UserId} index={SegmentIndex} course={CourseId} nodes={Nodes} edges={Edges} assignments={Assignments} courses={Courses} elapsedMs={ElapsedMs:F2}",
                    courseId,
                    userId.Value,
                    segmentIndex,
                    segment.CourseId,
                    segment.Nodes.Length,
                    segment.Edges.Length,
                    segment.Assignments.Length,
                    segment.Courses.Length,
                    (DateTimeOffset.UtcNow - startedAt).TotalMilliseconds);
            }

            TaskForgeDebugTrace.Map("HTTP_STREAM_END",
                ("user", userId.Value),
                ("requestedCourse", courseId),
                ("status", 200),
                ("segments", segmentIndex),
                ("projectionToken", session.Meta.ProjectionToken),
                ("revision", session.Meta.ProjectionRevision));
            await http.Response.WriteAsync(JsonSerializer.Serialize(new { type = "done", data = new { session.Meta.ProjectionToken, session.Meta.ProjectionRevision } }, jsonOptions) + "\n", ct);
            await http.Response.Body.FlushAsync(ct);
            logger.LogInformation(
                "TFDBG MAP HTTP STREAM DONE requested={RequestedCourseId} user={UserId} segments={Segments} durationMs={DurationMs:F2}",
                courseId,
                userId.Value,
                segmentIndex,
                (DateTimeOffset.UtcNow - startedAt).TotalMilliseconds);
        });

        app.MapPost("/api/courses/{courseId:guid}/learning-map/delta", async (
            Guid courseId,
            LearningMapDeltaRequest request,
            HttpContext http,
            IConfiguration cfg,
            CourseMapProjectionService projection,
            CancellationToken ct) =>
        {
            var userId = TaskForgeRequestSecurity.UserId(http, cfg);
            if (!userId.HasValue) return Microsoft.AspNetCore.Http.Results.Unauthorized();

            TaskForgeDebugTrace.Map("HTTP_DELTA_BEGIN",
                ("user", userId.Value),
                ("requestedCourse", courseId),
                ("projectionToken", request.ProjectionToken),
                ("changedAssignment", request.ChangedAssignmentId),
                ("editorBypass", IsEditor(http, cfg)),
                ("referer", http.Request.Headers["Referer"].FirstOrDefault()));
            var delta = await projection.CreateDeltaAsync(
                courseId,
                userId.Value,
                IsEditor(http, cfg),
                new CourseMapProjectionService.DeltaRequest(request.ProjectionToken, request.ChangedAssignmentId),
                ct);
            if (delta is null)
            {
                TaskForgeDebugTrace.Map("HTTP_DELTA_END", ("user", userId.Value), ("requestedCourse", courseId), ("status", 404));
                return Microsoft.AspNetCore.Http.Results.NotFound(new { message = "Курс не найден или ещё не открыт.", code = "COURSE_NOT_AVAILABLE" });
            }
            TaskForgeDebugTrace.Map("HTTP_DELTA_END",
                ("user", userId.Value),
                ("requestedCourse", courseId),
                ("status", 200),
                ("resetRequired", delta.ResetRequired),
                ("projectionToken", delta.ProjectionToken),
                ("revision", delta.ProjectionRevision),
                ("version", delta.Version),
                ("nodeIdsAdded", TaskForgeDebugTrace.MapList(delta.NodesAdded.Select(x => x.ValueKind == JsonValueKind.Object && x.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null))),
                ("nodeIdsRemoved", TaskForgeDebugTrace.MapList(delta.NodeIdsRemoved)),
                ("edgeIdsAdded", TaskForgeDebugTrace.MapList(delta.EdgesAdded.Select(x => x.ValueKind == JsonValueKind.Object && x.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null))),
                ("edgeIdsRemoved", TaskForgeDebugTrace.MapList(delta.EdgeIdsRemoved)),
                ("assignmentIdsChanged", TaskForgeDebugTrace.MapList(delta.AssignmentsChanged.Select(x => x.Id))),
                ("openedCourseIds", TaskForgeDebugTrace.MapList(delta.OpenedCourseIds)));
            http.Response.Headers.CacheControl = "no-store";
            return Microsoft.AspNetCore.Http.Results.Ok(delta);
        });


        app.MapGet("/api/courses/{courseId:guid}/assignments/tree", async (Guid courseId, HttpContext http, IConfiguration cfg, TasksDbContext db, IHttpClientFactory clients, CancellationToken ct) =>
        {
            var includeHidden = IsEditor(http, cfg);
            var userId = TaskForgeRequestSecurity.UserId(http, cfg);
            if (!includeHidden)
            {
                http.Response.Headers.CacheControl = "no-store";
                if (!userId.HasValue) return Microsoft.AspNetCore.Http.Results.Unauthorized();
                var evaluation = await CourseMapProgressionService.LoadEvaluationAsync(courseId, userId.Value, db, clients, cfg, ct);
                if (evaluation == null)
                    return Microsoft.AspNetCore.Http.Results.NotFound(new { message = "Курс не найден или ещё не открыт по прогрессии.", code = "COURSE_NOT_AVAILABLE" });

                var visibleIds = evaluation.VisibleAssignmentIds;
                var rows = await db.Assignments.AsNoTracking()
                    .Where(x => evaluation.RequestedSubtreeCourseIds.Contains(x.CourseId) && visibleIds.Contains(x.Id))
                    .OrderBy(x => x.CourseId)
                    .ThenBy(x => x.Sort)
                    .ThenBy(x => x.CreatedAt)
                    .ToListAsync(ct);
                return Microsoft.AspNetCore.Http.Results.Ok(rows.Select(x => ToDto(x, false, evaluation.SolvedAssignmentIds.Contains(x.Id))).ToList());
            }

            var tree = await GetInternalAsync<CourseTreeResponse>(
                clients,
                cfg,
                ServiceUrl(cfg, "EducationApi", "http://education-api:8080"),
                $"/api/internal/courses/{courseId:D}/tree",
                ct);
            if (tree == null || tree.CourseIds.Length == 0 || tree.Courses.All(x => x.Id != courseId))
                return Microsoft.AspNetCore.Http.Results.Json(new { message = "Не удалось получить дерево курса.", code = "COURSE_TREE_UNAVAILABLE" }, statusCode: StatusCodes.Status503ServiceUnavailable);

            var requestedIds = tree.CourseIds.Where(x => x != Guid.Empty).Distinct().Take(5000).ToArray();
            var editorRows = await db.Assignments.AsNoTracking()
                .Where(x => requestedIds.Contains(x.CourseId))
                .OrderBy(x => x.CourseId)
                .ThenBy(x => x.Sort)
                .ThenBy(x => x.CreatedAt)
                .ToListAsync(ct);
            var editorSolvedIds = userId.HasValue
                ? await LoadSolvedAssignmentIdsAsync(userId.Value, editorRows.Select(x => x.Id), db, clients, cfg, ct)
                : new HashSet<Guid>();
            return Microsoft.AspNetCore.Http.Results.Ok(editorRows.Select(x => ToDto(x, true, editorSolvedIds.Contains(x.Id))).ToList());
        });


        app.MapPost("/api/assignments/course-progress", async (CourseIdsRequest request, HttpContext http, IConfiguration cfg, TasksDbContext db, IHttpClientFactory clients, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            var requestedIds = (request.CourseIds ?? Array.Empty<Guid>())
                .Where(x => x != Guid.Empty)
                .Distinct()
                .Take(2000)
                .ToArray();

            if (requestedIds.Length == 0)
            {
                return Microsoft.AspNetCore.Http.Results.Ok(Array.Empty<CourseAssignmentProgressDto>());
            }

            var includeHidden = IsEditor(http, cfg);
            var userId = TaskForgeRequestSecurity.UserId(http, cfg);
            var allowedIds = requestedIds;
            var accessByCourseId = new Dictionary<Guid, CourseAccessDto>();
            var progressionByRootId = new Dictionary<Guid, CourseMapProgressionService.Evaluation?>();

            if (!includeHidden)
            {
                if (!userId.HasValue)
                {
                    return Microsoft.AspNetCore.Http.Results.Ok(Array.Empty<CourseAssignmentProgressDto>());
                }

                var accessRows = await LoadCourseAccessRowsAsync(requestedIds, userId.Value, clients, cfg, ct);
                accessByCourseId = accessRows
                    .Where(x => x.CanView)
                    .GroupBy(x => x.CourseId)
                    .ToDictionary(x => x.Key, x => x.First());

                // Progress counters must obey the same graph gates as the map itself.
                // Otherwise a fully hidden branch could still leak its number of tasks
                // through the X/Y badge on the course card. Evaluate once per distinct
                // root map, not once per requested child course.
                var gatedRootIds = accessByCourseId.Values
                    .Where(x => x.HasProgressionRules && x.RootCourseId != Guid.Empty)
                    .Select(x => x.RootCourseId)
                    .Distinct()
                    .ToArray();

                foreach (var rootCourseId in gatedRootIds)
                {
                    progressionByRootId[rootCourseId] = await CourseMapProgressionService.LoadEvaluationAsync(
                        rootCourseId,
                        userId.Value,
                        db,
                        clients,
                        cfg,
                        ct);
                }

                allowedIds = requestedIds
                    .Where(id => accessByCourseId.TryGetValue(id, out var courseAccess)
                        && (!courseAccess.HasProgressionRules
                            || (progressionByRootId.TryGetValue(courseAccess.RootCourseId, out var evaluation)
                                && evaluation?.VisibleCourseIds.Contains(id) == true)))
                    .ToArray();
            }

            if (allowedIds.Length == 0)
            {
                return Microsoft.AspNetCore.Http.Results.Ok(Array.Empty<CourseAssignmentProgressDto>());
            }

            var query = db.Assignments.AsNoTracking().Where(x => allowedIds.Contains(x.CourseId));
            if (!includeHidden) query = query.Where(x => x.IsVisible);

            var rows = await query
                .Select(x => new { x.Id, x.CourseId })
                .ToListAsync(ct);

            if (!includeHidden)
            {
                rows = rows.Where(row =>
                {
                    if (!accessByCourseId.TryGetValue(row.CourseId, out var courseAccess) || !courseAccess.HasProgressionRules)
                        return true;
                    return progressionByRootId.TryGetValue(courseAccess.RootCourseId, out var evaluation)
                        && evaluation?.VisibleAssignmentIds.Contains(row.Id) == true;
                }).ToList();
            }

            var solvedIds = userId.HasValue
                ? await LoadSolvedAssignmentIdsAsync(userId.Value, rows.Select(x => x.Id), db, clients, cfg, ct)
                : new HashSet<Guid>();

            var grouped = rows
                .GroupBy(x => x.CourseId)
                .ToDictionary(
                    g => g.Key,
                    g =>
                    {
                        var total = g.Count();
                        var solved = g.Count(x => solvedIds.Contains(x.Id));
                        var percent = total > 0 ? (int)System.Math.Round((double)solved / total * 100) : 0;
                        return new CourseAssignmentProgressDto(g.Key, total, solved, percent, total > 0 && solved == total);
                    });

            return Microsoft.AspNetCore.Http.Results.Ok(allowedIds.Select(id =>
                grouped.TryGetValue(id, out var progress)
                    ? progress
                    : new CourseAssignmentProgressDto(id, 0, 0, 0, false)).ToList());
        });


        app.MapGet("/api/courses/{courseId:guid}/assignments/export-json", async (
            Guid courseId,
            bool? includeIds,
            bool? includeContent,
            bool? includeChecks,
            bool? includeVisibility,
            bool? includeConnections,
            bool? includeConnectionAccess,
            bool? includeLayout,
            bool? includeGuide,
            HttpContext http, IConfiguration cfg, TasksDbContext db, IHttpClientFactory clients, CancellationToken ct) =>
        {
            if (!IsEditor(http, cfg))
            {
                return Microsoft.AspNetCore.Http.Results.Json(new { message = "Для экспорта заданий нужны права редактора.", code = "EDITOR_REQUIRED" }, statusCode: StatusCodes.Status403Forbidden);
            }

            var educationBaseUrl = ServiceUrl(cfg, "EducationApi", "http://education-api:8080");
            var tree = await GetInternalAsync<CourseTreeResponse>(
                clients,
                cfg,
                educationBaseUrl,
                $"/api/internal/courses/{courseId:D}/tree",
                ct);
            if (tree == null || tree.CourseIds.Length == 0)
            {
                return Microsoft.AspNetCore.Http.Results.Json(
                    new { message = "Не удалось получить структуру курса для экспорта.", code = "COURSE_TREE_UNAVAILABLE" },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            var exportCourseIds = tree.CourseIds.Where(x => x != Guid.Empty).Distinct().ToArray();
            var rows = await db.Assignments.AsNoTracking()
                .Where(x => exportCourseIds.Contains(x.CourseId))
                .OrderBy(x => x.CourseId)
                .ThenBy(x => x.Sort)
                .ThenBy(x => x.CreatedAt)
                .ToListAsync(ct);
            var map = await GetInternalAsync<CourseMapInternalResponse>(
                clients,
                cfg,
                educationBaseUrl,
                $"/api/internal/courses/{courseId:D}/map",
                ct);
            if (map == null)
            {
                return Microsoft.AspNetCore.Http.Results.Json(
                    new { message = "Не удалось получить карту курса для экспорта.", code = "COURSE_MAP_UNAVAILABLE" },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            var exportOptions = new GraphExportOptions(
                includeIds ?? true,
                includeContent ?? true,
                includeChecks ?? true,
                includeVisibility ?? true,
                includeConnections ?? true,
                includeConnectionAccess ?? true,
                includeLayout ?? true,
                includeGuide ?? false);
            var sql = await SqlTaskGraphService.Export(db, rows, exportOptions, ct);
            return Microsoft.AspNetCore.Http.Results.Json(BuildExport(courseId, rows, tree, map, exportOptions, sql), JsonOptions());
        }).AddEndpointFilter<SqlEndpointFilter>();

        app.MapPost("/api/courses/{courseId:guid}/assignments", async (Guid courseId, AssignmentRequest request, TasksDbContext db, IHttpClientFactory clients, IConfiguration cfg, CancellationToken ct) =>
        {
            var maxSort = await db.Assignments.Where(x => x.CourseId == courseId).Select(x => (int?)x.Sort).MaxAsync(ct) ?? -1;
            var assignment = await BuildAssignmentEntityAsync(courseId, request, maxSort + 1, clients, cfg, ct);
            db.Assignments.Add(assignment);
            await db.SaveChangesAsync(ct);
            return Microsoft.AspNetCore.Http.Results.Ok(ToDto(assignment, includeSensitive: true));
        });

        app.MapPost("/api/courses/{courseId:guid}/assignments/import-json", async (Guid courseId, JsonElement payload, HttpContext http, IConfiguration cfg, TasksDbContext db, IHttpClientFactory clients, CancellationToken ct) =>
        {
            if (!IsEditor(http, cfg))
            {
                return Microsoft.AspNetCore.Http.Results.Json(new { message = "Для импорта заданий нужны права редактора.", code = "EDITOR_REQUIRED" }, statusCode: StatusCodes.Status403Forbidden);
            }

            var importOptions = ReadGraphImportOptions(payload);
            var graphPayload = ReadGraphImportPayload(payload);
            ParsedGraph? taskGraph = null;
            Dictionary<string, Guid>? graphCourseIds = null;
            HashSet<Guid>? graphSubtreeCourseIds = null;
            List<JsonElement> sourceItems;
            if (LooksLikeCanonicalGraph(graphPayload))
            {
                var parsed = ParseAndValidate(graphPayload);
                if (parsed.Issues.Count > 0 || parsed.Graph == null)
                {
                    return Microsoft.AspNetCore.Http.Results.Json(new
                    {
                        message = "Импорт остановлен: граф заданий содержит ошибки.",
                        code = "TASK_GRAPH_VALIDATION_FAILED",
                        issues = parsed.Issues.Select(x => new { path = x.Path, message = x.Message }).ToList()
                    }, statusCode: StatusCodes.Status400BadRequest);
                }
                taskGraph = parsed.Graph;
                importOptions = importOptions with
                {
                    UpdateContent = importOptions.UpdateContent && taskGraph.Scopes.Contains("content"),
                    UpdateChecks = importOptions.UpdateChecks && taskGraph.Scopes.Contains("checks"),
                    UpdateVisibility = importOptions.UpdateVisibility && taskGraph.Scopes.Contains("visibility"),
                    UpdateConnections = importOptions.UpdateConnections && taskGraph.Scopes.Contains("connections"),
                    UpdateConnectionAccess = importOptions.UpdateConnectionAccess && taskGraph.Scopes.Contains("connectionAccess"),
                    UpdateLayout = importOptions.UpdateLayout && taskGraph.Scopes.Contains("layout") && taskGraph.Layout.HasValue
                };

                var importTree = await GetInternalAsync<CourseTreeResponse>(
                    clients,
                    cfg,
                    ServiceUrl(cfg, "EducationApi", "http://education-api:8080"),
                    $"/api/internal/courses/{courseId:D}/tree",
                    ct);
                if (importTree == null || importTree.CourseIds.Length == 0)
                {
                    return Microsoft.AspNetCore.Http.Results.Json(
                        new { message = "Не удалось получить структуру курса для импорта.", code = "COURSE_TREE_UNAVAILABLE" },
                        statusCode: StatusCodes.Status503ServiceUnavailable);
                }
                graphSubtreeCourseIds = importTree.CourseIds.Where(x => x != Guid.Empty).ToHashSet();
                graphSubtreeCourseIds.Add(courseId);
                graphCourseIds = new Dictionary<string, Guid>(StringComparer.Ordinal) { [CourseReference] = courseId };
                var courseReferenceIssues = new List<object>();
                for (var courseIndex = 0; courseIndex < taskGraph.Courses.Count; courseIndex++)
                {
                    var graphCourse = taskGraph.Courses[courseIndex];
                    var desiredId = graphCourse.Id != Guid.Empty ? graphCourse.Id : Guid.NewGuid();
                    if (desiredId == courseId)
                    {
                        courseReferenceIssues.Add(new
                        {
                            path = $"$.courses[{courseIndex}].id",
                            message = "Текущий курс нельзя повторно объявлять как вложенный. Используйте $course."
                        });
                        continue;
                    }
                    graphCourseIds[graphCourse.Key] = desiredId;
                }
                if (courseReferenceIssues.Count > 0)
                {
                    return Microsoft.AspNetCore.Http.Results.Json(new
                    {
                        message = "Импорт остановлен: некорректные ссылки на вложенные курсы.",
                        code = "TASK_GRAPH_COURSE_INVALID",
                        issues = courseReferenceIssues
                    }, statusCode: StatusCodes.Status400BadRequest);
                }
                sourceItems = taskGraph.Tasks.Select(x => x.Source).ToList();
            }
            else
            {
                sourceItems = ExtractAssignmentImportItems(graphPayload).ToList();
            }

            if (sourceItems.Count == 0 && taskGraph == null)
            {
                return Microsoft.AspNetCore.Http.Results.Json(new { message = "JSON не содержит заданий.", code = "IMPORT_EMPTY" }, statusCode: StatusCodes.Status400BadRequest);
            }
            if (sourceItems.Count == 0 && taskGraph != null
                && !importOptions.UpdateConnections
                && !importOptions.UpdateConnectionAccess
                && !importOptions.UpdateLayout)
            {
                return Microsoft.AspNetCore.Http.Results.Json(new { message = "В выбранных разделах JSON нечего импортировать.", code = "IMPORT_EMPTY" }, statusCode: StatusCodes.Status400BadRequest);
            }
            if (sourceItems.Count > MaxTasks)
            {
                return Microsoft.AspNetCore.Http.Results.Json(new { message = $"За один импорт можно обработать не больше {MaxTasks} заданий.", code = "IMPORT_TOO_LARGE", count = sourceItems.Count }, statusCode: StatusCodes.Status400BadRequest);
            }

            var requests = new List<AssignmentRequest>();
            var issues = new List<object>();
            for (var i = 0; i < sourceItems.Count; i++)
            {
                try
                {
                    requests.Add(AssignmentRequestFromJson(sourceItems[i]));
                }
                catch (Exception ex)
                {
                    issues.Add(new { index = i + 1, key = taskGraph?.Tasks[i].Key, issues = new[] { $"Не удалось прочитать объект задания: {ex.Message}" } });
                }
            }

            if (issues.Count > 0)
            {
                return Microsoft.AspNetCore.Http.Results.Json(new { message = "Импорт остановлен: в JSON есть ошибки.", code = "IMPORT_VALIDATION_FAILED", issues }, statusCode: StatusCodes.Status400BadRequest);
            }

            var ids = requests.Select(x => x.Id).Where(x => x.HasValue && x.Value != Guid.Empty).Select(x => x!.Value).Distinct().ToList();
            var existingRows = ids.Count == 0
                ? new List<Assignment>()
                : await db.Assignments.Where(x => ids.Contains(x.Id)).ToListAsync(ct);
            var existingRowById = existingRows.ToDictionary(x => x.Id);

            for (var i = 0; i < requests.Count; i++)
            {
                var req = requests[i];
                var isExisting = req.Id.HasValue && req.Id.Value != Guid.Empty && existingRowById.ContainsKey(req.Id.Value);
                var validationRequest = taskGraph != null && isExisting
                    ? FilterExistingImportRequest(req, importOptions)
                    : isExisting
                        ? req
                        : req with { Id = null };
                var itemIssues = ValidateImportedAssignment(validationRequest, i + 1).ToList();
                if (taskGraph != null && !isExisting && !taskGraph.Scopes.Contains("content"))
                    itemIssues.Add("Для создания нового задания JSON должен содержать scope content и полноценное описание задания.");
                if (itemIssues.Count == 0) continue;
                issues.Add(new
                {
                    index = i + 1,
                    key = taskGraph?.Tasks[i].Key,
                    id = req.Id,
                    title = req.Title,
                    issues = itemIssues
                });
            }

            if (issues.Count > 0)
            {
                return Microsoft.AspNetCore.Http.Results.Json(new { message = "Импорт остановлен: в JSON есть ошибки.", code = "IMPORT_VALIDATION_FAILED", issues }, statusCode: StatusCodes.Status400BadRequest);
            }
            var allowedImportCourseIds = taskGraph != null && graphSubtreeCourseIds != null
                ? graphSubtreeCourseIds
                : new HashSet<Guid> { courseId };
            var existingById = existingRows
                .Where(x => allowedImportCourseIds.Contains(x.CourseId))
                .ToDictionary(x => x.Id);
            var usedIds = existingRows.Select(x => x.Id).ToHashSet();

            if (taskGraph != null && graphCourseIds != null)
            {
                var idIssues = new List<object>();
                for (var index = 0; index < taskGraph.Tasks.Count; index++)
                {
                    var id = requests[index].Id;
                    if (!id.HasValue) continue;
                    if (!existingRowById.TryGetValue(id.Value, out var existingRow))
                    {
                        // Свободный UUID разрешён: новое задание будет создано именно с этим id.
                        // Это делает экспорт/импорт графа детерминированным и позволяет повторный
                        // импорт того же JSON автоматически превратить create в update.
                        continue;
                    }
                    if (!allowedImportCourseIds.Contains(existingRow.CourseId))
                    {
                        idIssues.Add(new { path = $"$.tasks[{index}].id", message = "Задание с таким id находится вне импортируемого поддерева." });
                        continue;
                    }
                    var expectedCourseId = graphCourseIds.GetValueOrDefault(taskGraph.Tasks[index].CourseRef, courseId);
                    if (existingRow.CourseId != expectedCourseId)
                    {
                        idIssues.Add(new { path = $"$.tasks[{index}].course", message = "Поле course не совпадает с курсом существующего задания. JSON-импорт не переносит задания между курсами." });
                    }
                }
                if (idIssues.Count > 0)
                {
                    return Microsoft.AspNetCore.Http.Results.Json(new
                    {
                        message = "Импорт остановлен: некоторые id или course нельзя использовать в этом поддереве.",
                        code = "TASK_GRAPH_ASSIGNMENT_ID_INVALID",
                        issues = idIssues
                    }, statusCode: StatusCodes.Status400BadRequest);
                }
            }

            var createdCourseCount = 0;
            if (taskGraph != null && graphCourseIds != null && taskGraph.Courses.Count > 0)
            {
                var educationBaseUrl = ServiceUrl(cfg, "EducationApi", "http://education-api:8080");
                var courseEnsure = await PostInternalAsync<CourseGraphImportEnsureResponse>(
                    clients,
                    cfg,
                    educationBaseUrl,
                    "/api/internal/courses/import-ensure",
                    new
                    {
                        rootCourseId = courseId,
                        ownerId = RequireUser(http, cfg),
                        courses = taskGraph.Courses.Select(x => new
                        {
                            key = x.Key,
                            id = graphCourseIds[x.Key],
                            title = x.Title
                        }).ToArray()
                    },
                    ct);
                if (courseEnsure == null || courseEnsure.Courses.Count != taskGraph.Courses.Count)
                {
                    return Microsoft.AspNetCore.Http.Results.Json(new
                    {
                        message = "Импорт остановлен: не удалось разрешить или создать вложенные курсы. Проверьте, что UUID не занят чужим курсом.",
                        code = "TASK_GRAPH_COURSE_RESOLVE_FAILED"
                    }, statusCode: StatusCodes.Status400BadRequest);
                }
                foreach (var resolvedCourse in courseEnsure.Courses)
                {
                    if (string.IsNullOrWhiteSpace(resolvedCourse.Key) || resolvedCourse.Id == Guid.Empty) continue;
                    graphCourseIds[resolvedCourse.Key] = resolvedCourse.Id;
                    graphSubtreeCourseIds?.Add(resolvedCourse.Id);
                    if (resolvedCourse.Created) createdCourseCount++;
                }
            }

            var targetCourseIds = taskGraph != null && graphCourseIds != null
                ? taskGraph.Tasks.Select(x => graphCourseIds.GetValueOrDefault(x.CourseRef, courseId)).Append(courseId).Distinct().ToArray()
                : new[] { courseId };
            var maxSortRows = await db.Assignments.AsNoTracking()
                .Where(x => targetCourseIds.Contains(x.CourseId))
                .GroupBy(x => x.CourseId)
                .Select(g => new { CourseId = g.Key, MaxSort = g.Max(x => x.Sort) })
                .ToListAsync(ct);
            var nextSortByCourse = targetCourseIds.ToDictionary(
                id => id,
                id => (maxSortRows.FirstOrDefault(x => x.CourseId == id)?.MaxSort ?? -1) + 1);
            var created = new List<Assignment>();
            var updated = new List<Assignment>();
            var processed = new List<(string? Key, string CourseRef, Assignment Assignment, string Action)>();
            var ratingAffectedAssignmentIds = new HashSet<Guid>();
            await using var sqlImportTransaction = taskGraph?.SourceSchemaVersion == SchemaVersion
                ? await db.Database.BeginTransactionAsync(ct) : null;

            for (var i = 0; i < requests.Count; i++)
            {
                var request = requests[i];
                var key = taskGraph?.Tasks[i].Key;
                var taskCourseRef = taskGraph?.Tasks[i].CourseRef ?? CourseReference;
                var targetCourseId = taskGraph != null && graphCourseIds != null
                    ? graphCourseIds.GetValueOrDefault(taskCourseRef, courseId)
                    : courseId;
                if (request.Id.HasValue && existingById.TryGetValue(request.Id.Value, out var existing))
                {
                    var oldRating = existing.Rating;
                    var oldVisible = existing.IsVisible;
                    var filteredRequest = FilterExistingImportRequest(request, importOptions);
                    if (!string.IsNullOrWhiteSpace(filteredRequest.Type) && filteredRequest.Type != existing.Type
                        && (filteredRequest.Type == SqlTaskTypes.SqlTest || existing.Type == SqlTaskTypes.SqlTest))
                        throw new ArgumentException("Create a separate SQL assignment instead of changing a populated assignment type.");
                    await ApplyAssignmentRequestAsync(existing, filteredRequest, clients, cfg, ct);
                    if (oldRating != existing.Rating || oldVisible != existing.IsVisible) ratingAffectedAssignmentIds.Add(existing.Id);
                    updated.Add(existing);
                    processed.Add((key, taskCourseRef, existing, "updated"));
                    continue;
                }

                var createRequest = request;
                if (createRequest.Id.HasValue && usedIds.Contains(createRequest.Id.Value))
                {
                    createRequest = createRequest with { Id = null };
                }

                var nextSort = nextSortByCourse.GetValueOrDefault(targetCourseId, 0);
                nextSortByCourse[targetCourseId] = nextSort + 1;
                var assignment = await BuildAssignmentEntityAsync(targetCourseId, createRequest, nextSort, clients, cfg, ct);
                created.Add(assignment);
                processed.Add((key, taskCourseRef, assignment, "created"));
            }

            if (created.Count > 0) db.Assignments.AddRange(created);
            await db.SaveChangesAsync(ct);
            if (taskGraph?.SourceSchemaVersion == SchemaVersion)
            {
                var (sqlUser, sqlAdmin) = SqlTaskService.Editor(http, cfg);
                await SqlTaskGraphService.Import(db, graphPayload, processed, importOptions, sqlUser, sqlAdmin, ct);
                foreach (var item in processed.Where(x => x.Assignment.Type == SqlTaskTypes.SqlTest && x.Assignment.IsVisible))
                    if (!await SqlTaskService.HasPublishedRevision(db, item.Assignment.Id, ct)) item.Assignment.IsVisible = false;
                await db.SaveChangesAsync(ct);
            }
            if (sqlImportTransaction is not null) await sqlImportTransaction.CommitAsync(ct);

            foreach (var affectedId in ratingAffectedAssignmentIds)
            {
                var users = await db.Attempts.AsNoTracking()
                    .Where(x => x.TaskAssignmentId == affectedId)
                    .Select(x => x.UserId)
                    .Distinct()
                    .ToListAsync(ct);
                await MarkAssignmentRatingDirtyInSolutionsAsync(clients, cfg, affectedId, users, "assignment-import-updated", ct);
            }

            JsonObject? importedTaskGraph = null;
            if (taskGraph != null)
            {
                var mappedTasks = new JsonArray();
                foreach (var item in processed)
                {
                    mappedTasks.Add(new JsonObject
                    {
                        ["key"] = item.Key,
                        ["course"] = item.CourseRef,
                        ["assignmentId"] = item.Assignment.Id.ToString(),
                        ["action"] = item.Action
                    });
                }

                var mappedConnections = new JsonArray();
                foreach (var connection in taskGraph.Connections)
                    mappedConnections.Add(connection.ToJson());

                var mappedCourses = new JsonArray();
                foreach (var graphCourse in taskGraph.Courses)
                {
                    var resolvedCourseId = graphCourseIds?.GetValueOrDefault(graphCourse.Key, Guid.Empty) ?? Guid.Empty;
                    mappedCourses.Add(new JsonObject
                    {
                        ["key"] = graphCourse.Key,
                        ["id"] = resolvedCourseId == Guid.Empty ? null : resolvedCourseId.ToString("D"),
                        ["title"] = graphCourse.Title
                    });
                }

                importedTaskGraph = new JsonObject
                {
                    ["schemaVersion"] = SchemaVersion,
                    ["format"] = Format,
                    ["scopes"] = BuildScopesJson(taskGraph.Scopes),
                    ["courses"] = mappedCourses,
                    ["tasks"] = mappedTasks,
                    ["datasets"] = new JsonArray(),
                    ["connections"] = mappedConnections,
                    ["layout"] = taskGraph.Layout.HasValue ? JsonNode.Parse(taskGraph.Layout.Value.GetRawText()) : null,
                    ["apply"] = new JsonObject
                    {
                        ["content"] = importOptions.UpdateContent,
                        ["checks"] = importOptions.UpdateChecks,
                        ["visibility"] = importOptions.UpdateVisibility,
                        ["connections"] = importOptions.UpdateConnections,
                        ["connectionAccess"] = importOptions.UpdateConnectionAccess,
                        ["layout"] = importOptions.UpdateLayout
                    }
                };
            }

            return Microsoft.AspNetCore.Http.Results.Ok(new
            {
                createdCount = created.Count,
                updatedCount = updated.Count,
                createdCourseCount,
                totalCount = processed.Count,
                assignments = processed.Select(x => ToDto(x.Assignment, includeSensitive: true)).ToList(),
                taskGraph = importedTaskGraph
            });
        }).AddEndpointFilter<SqlEndpointFilter>();

        app.MapGet("/api/assignments/{assignmentId:guid}/solve-shell", async (Guid assignmentId, HttpContext http, IConfiguration cfg, TasksDbContext db, IHttpClientFactory clients, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            var assignment = await db.Assignments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == assignmentId, ct);
            if (assignment == null) return Microsoft.AspNetCore.Http.Results.NotFound(new { message = "Задание не найдено.", code = "ASSIGNMENT_NOT_FOUND" });
            var includeSensitive = IsEditor(http, cfg);
            if (!includeSensitive && !await CanUserAccessAssignmentAsync(assignment, http, cfg, db, clients, ct)) return Microsoft.AspNetCore.Http.Results.NotFound(new { message = "Задание не найдено.", code = "ASSIGNMENT_NOT_FOUND" });

            var userId = TaskForgeRequestSecurity.UserId(http, cfg);
            var solved = userId.HasValue
                ? await LoadSolvedAssignmentIdsAsync(userId.Value, new[] { assignment.Id }, db, clients, cfg, ct)
                : new HashSet<Guid>();

            return Microsoft.AspNetCore.Http.Results.Ok(ToSolveShellDto(assignment, includeSensitive, solved.Contains(assignment.Id)));
        });

        app.MapGet("/api/assignments/{assignmentId:guid}/statement", async (Guid assignmentId, HttpContext http, IConfiguration cfg, TasksDbContext db, IHttpClientFactory clients, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            var assignment = await db.Assignments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == assignmentId, ct);
            if (assignment == null) return Microsoft.AspNetCore.Http.Results.NotFound(new { message = "Задание не найдено.", code = "ASSIGNMENT_NOT_FOUND" });
            var includeSensitive = IsEditor(http, cfg);
            if (!includeSensitive && !await CanUserAccessAssignmentAsync(assignment, http, cfg, db, clients, ct)) return Microsoft.AspNetCore.Http.Results.NotFound(new { message = "Задание не найдено.", code = "ASSIGNMENT_NOT_FOUND" });
            return Microsoft.AspNetCore.Http.Results.Ok(ToSolveStatementDto(assignment, includeSensitive));
        });

        app.MapGet("/api/assignments/{assignmentId:guid}/tests", async (Guid assignmentId, HttpContext http, IConfiguration cfg, TasksDbContext db, IHttpClientFactory clients, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            var assignment = await db.Assignments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == assignmentId, ct);
            if (assignment == null) return Microsoft.AspNetCore.Http.Results.NotFound(new { message = "Задание не найдено.", code = "ASSIGNMENT_NOT_FOUND" });
            var includeSensitive = IsEditor(http, cfg);
            if (!includeSensitive && !await CanUserAccessAssignmentAsync(assignment, http, cfg, db, clients, ct)) return Microsoft.AspNetCore.Http.Results.NotFound(new { message = "Задание не найдено.", code = "ASSIGNMENT_NOT_FOUND" });
            return Microsoft.AspNetCore.Http.Results.Ok(ToSolveTestsDto(assignment, includeSensitive));
        });

        app.MapGet("/api/assignments/{assignmentId:guid}", async (Guid assignmentId, HttpContext http, IConfiguration cfg, TasksDbContext db, IHttpClientFactory clients, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            var assignment = await db.Assignments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == assignmentId, ct);
            if (assignment == null) return Microsoft.AspNetCore.Http.Results.NotFound(new { message = "Задание не найдено.", code = "ASSIGNMENT_NOT_FOUND" });
            var includeSensitive = IsEditor(http, cfg);
            if (!includeSensitive && !await CanUserAccessAssignmentAsync(assignment, http, cfg, db, clients, ct)) return Microsoft.AspNetCore.Http.Results.NotFound(new { message = "Задание не найдено.", code = "ASSIGNMENT_NOT_FOUND" });
            return Microsoft.AspNetCore.Http.Results.Ok(ToDto(assignment, includeSensitive));
        });

        app.MapGet("/api/assignments/{assignmentId:guid}/edit", async (Guid assignmentId, HttpContext http, IConfiguration cfg, TasksDbContext db) =>
        {
            if (!IsEditor(http, cfg)) return Microsoft.AspNetCore.Http.Results.Json(new { message = "Для редактирования нужны права редактора.", code = "EDITOR_REQUIRED" }, statusCode: StatusCodes.Status403Forbidden);
            var assignment = await db.Assignments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == assignmentId);
            return assignment == null ? Microsoft.AspNetCore.Http.Results.NotFound(new { message = "Задание не найдено.", code = "ASSIGNMENT_NOT_FOUND" }) : Microsoft.AspNetCore.Http.Results.Ok(ToDto(assignment, includeSensitive: true));
        });

        app.MapPut("/api/assignments/{assignmentId:guid}", async (Guid assignmentId, AssignmentRequest request, TasksDbContext db, IHttpClientFactory clients, IConfiguration cfg, CancellationToken ct) =>
        {
            var assignment = await db.Assignments.FindAsync(assignmentId);
            if (assignment == null) return Microsoft.AspNetCore.Http.Results.NotFound(new { message = "Задание не найдено.", code = "ASSIGNMENT_NOT_FOUND" });
            if (!string.IsNullOrWhiteSpace(request.Type) && request.Type != assignment.Type
                && (request.Type == SqlTaskTypes.SqlTest || assignment.Type == SqlTaskTypes.SqlTest))
                return Microsoft.AspNetCore.Http.Results.Conflict(new { code = "SQL_TYPE_IMMUTABLE", message = "Create a separate SQL assignment instead of changing its type." });
            if (assignment.Type == SqlTaskTypes.SqlTest && (request.IsHidden.HasValue ? !request.IsHidden.Value : request.IsVisible == true) && !await SqlTaskService.HasPublishedRevision(db, assignmentId, ct))
                return Microsoft.AspNetCore.Http.Results.Conflict(new { code = "SQL_NOT_VALIDATED", message = "Validate and publish a SQL revision before making it visible." });
            var oldRating = assignment.Rating;
            var oldVisible = assignment.IsVisible;
            await ApplyAssignmentRequestAsync(assignment, request, clients, cfg, ct);
            var affectsRating = oldRating != assignment.Rating || oldVisible != assignment.IsVisible;
            await db.SaveChangesAsync(ct);
            if (affectsRating)
            {
                var users = await db.Attempts.AsNoTracking()
                    .Where(x => x.TaskAssignmentId == assignmentId)
                    .Select(x => x.UserId)
                    .Distinct()
                    .ToListAsync(ct);
                await MarkAssignmentRatingDirtyInSolutionsAsync(clients, cfg, assignmentId, users, "assignment-updated", ct);
            }
            return Microsoft.AspNetCore.Http.Results.Ok(ToDto(assignment, includeSensitive: true));
        });

        app.MapDelete("/api/assignments/{assignmentId:guid}", async (Guid assignmentId, TasksDbContext db, IHttpClientFactory clients, IConfiguration cfg, CancellationToken ct) =>
        {
            var assignment = await db.Assignments.FindAsync([assignmentId], ct);
            if (assignment == null) return Microsoft.AspNetCore.Http.Results.NotFound(new { message = "Задание не найдено.", code = "ASSIGNMENT_NOT_FOUND" });
            if (assignment.Type == SqlTaskTypes.SqlTest && await db.SqlAssignmentSpecs.AnyAsync(x => x.AssignmentId == assignmentId, ct))
                return Microsoft.AspNetCore.Http.Results.Conflict(new { code = "SQL_HISTORY_RETAINED", message = "SQL revision history is retained for submissions. Hide the assignment instead of deleting it." });
            var attempts = await db.Attempts.Where(x => x.TaskAssignmentId == assignmentId).ToListAsync(ct);
            var users = attempts.Select(x => x.UserId).Where(x => x != Guid.Empty).Distinct().ToArray();
            db.Attempts.RemoveRange(attempts);
            db.Assignments.Remove(assignment);
            await db.SaveChangesAsync(ct);
            await MarkAssignmentRatingDirtyInSolutionsAsync(clients, cfg, assignmentId, users, "assignment-deleted", ct);
            return Microsoft.AspNetCore.Http.Results.Ok(new { message = "Задание удалено.", deleted = assignmentId });
        });

        app.MapPatch("/api/assignments/{assignmentId:guid}/sort", async (Guid assignmentId, SortRequest request, TasksDbContext db) =>
        {
            var assignment = await db.Assignments.FindAsync(assignmentId);
            if (assignment == null) return Microsoft.AspNetCore.Http.Results.NotFound();
            assignment.Sort = request.Sort;
            assignment.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
            return Microsoft.AspNetCore.Http.Results.Ok(ToDto(assignment, includeSensitive: true));
        });

        app.MapPatch("/api/assignments/{assignmentId:guid}/position", async (Guid assignmentId, PositionRequest request, TasksDbContext db) =>
        {
            var assignment = await db.Assignments.FindAsync(assignmentId);
            if (assignment == null) return Microsoft.AspNetCore.Http.Results.NotFound();
            var siblings = await db.Assignments.Where(x => x.CourseId == assignment.CourseId && x.Id != assignment.Id).OrderBy(x => x.Sort).ToListAsync();
            var pos = System.Math.Clamp((request.Position ?? siblings.Count + 1) - 1, 0, siblings.Count);
            siblings.Insert(pos, assignment);
            for (var i = 0; i < siblings.Count; i++) siblings[i].Sort = i;
            await db.SaveChangesAsync();
            return Microsoft.AspNetCore.Http.Results.Ok(ToDto(assignment, includeSensitive: true));
        });

        app.MapPatch("/api/assignments/{assignmentId:guid}/visibility", async (Guid assignmentId, VisibilityRequest request, TasksDbContext db, IHttpClientFactory clients, IConfiguration cfg, CancellationToken ct) =>
        {
            var assignment = await db.Assignments.FindAsync([assignmentId], ct);
            if (assignment == null) return Microsoft.AspNetCore.Http.Results.NotFound();
            if (assignment.Type == SqlTaskTypes.SqlTest && request.IsVisible && !await SqlTaskService.HasPublishedRevision(db, assignmentId, ct))
                return Microsoft.AspNetCore.Http.Results.Conflict(new { code = "SQL_NOT_VALIDATED", message = "Validate and publish a SQL revision before making it visible." });
            var changed = assignment.IsVisible != request.IsVisible;
            assignment.IsVisible = request.IsVisible;
            await db.SaveChangesAsync(ct);
            if (changed)
            {
                var users = await db.Attempts.AsNoTracking()
                    .Where(x => x.TaskAssignmentId == assignmentId)
                    .Select(x => x.UserId)
                    .Distinct()
                    .ToListAsync(ct);
                await MarkAssignmentRatingDirtyInSolutionsAsync(clients, cfg, assignmentId, users, "assignment-visibility", ct);
            }
            return Microsoft.AspNetCore.Http.Results.Ok(ToDto(assignment, includeSensitive: true));
        });

        return app;
    }
}
