using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using taskforge.Constants;
using taskforge.Data;
using taskforge.Data.Models.DTO;
using taskforge.Data.Models.DTO.AI;
using taskforge.Data.Models.DTO.TaskMaths;
using taskforge.Data.Models.DTO.TaskTests;
using taskforge.Data.Models.Entities.AI;
using taskforge.Services.Interfaces;

namespace taskforge.Services.AI;

public sealed partial class AiJobService : IAiJobService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly ApplicationDbContext _db;
    private readonly ILogger<AiJobService> _log;
    private readonly AiOptions _aiOptions;
    private readonly IAssignmentService _assignmentService;
    private readonly ITaskTestService _taskTestService;
    private readonly ITaskMathService _taskMathService;

    public AiJobService(
        ApplicationDbContext db,
        ILogger<AiJobService> log,
        IOptions<AiOptions> aiOptions,
        IAssignmentService assignmentService,
        ITaskTestService taskTestService,
        ITaskMathService taskMathService)
    {
        _db = db;
        _log = log;
        _aiOptions = aiOptions.Value;
        _assignmentService = assignmentService;
        _taskTestService = taskTestService;
        _taskMathService = taskMathService;
    }

    public async Task<AiJobListResponseDto> GetAdminJobsAsync(string? status, string? type, int page, int pageSize, CancellationToken ct = default)
    {
        page = page <= 0 ? 1 : page;
        pageSize = pageSize <= 0 ? 20 : Math.Min(pageSize, 100);
        status = string.IsNullOrWhiteSpace(status) ? null : status.Trim().ToLowerInvariant();
        type = string.IsNullOrWhiteSpace(type) ? null : type.Trim().ToLowerInvariant();

        var q = _db.AiJobs.AsNoTracking().Include(x => x.Files).AsQueryable();
        if (status != null) q = q.Where(x => x.Status.ToLower() == status);
        if (type != null) q = q.Where(x => x.Type.ToLower().Contains(type));

        var total = await q.CountAsync(ct);
        var items = await q.OrderByDescending(x => x.CreatedAtUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new AiJobListItemDto
            {
                Id = x.Id,
                Type = x.Type,
                Status = x.Status,
                Priority = x.Priority,
                ModelName = x.ModelName,
                WorkerId = x.WorkerId,
                CreatedByDisplayName = x.CreatedByDisplayName,
                TargetEntityType = x.TargetEntityType,
                TargetEntityId = x.TargetEntityId,
                CourseId = x.CourseId,
                CreatedAtUtc = x.CreatedAtUtc,
                StartedAtUtc = x.StartedAtUtc,
                CompletedAtUtc = x.CompletedAtUtc,
                NextAttemptAtUtc = x.NextAttemptAtUtc,
                ParentJobId = x.ParentJobId,
                StageCode = x.StageCode,
                StageLabel = x.StageLabel,
                StageOrder = x.StageOrder,
                RetryCount = x.RetryCount,
                ErrorText = x.ErrorText,
                FilesCount = x.Files.Count,
            })
            .ToListAsync(ct);

        return new AiJobListResponseDto { Total = total, Items = items };
    }

    public async Task<AiJobDetailsDto?> GetAdminJobAsync(Guid id, CancellationToken ct = default)
    {
        var job = await _db.AiJobs.AsNoTracking().Include(x => x.Files).Include(x => x.Artifacts).FirstOrDefaultAsync(x => x.Id == id, ct);
        return job == null ? null : MapDetails(job);
    }

    public async Task<AiJobDetailsDto> EnqueueAsync(CreateAiJobRequestDto request, Guid? createdByUserId, string? createdByDisplayName, CancellationToken ct = default)
    {
        var job = new AiJob
        {
            Id = Guid.NewGuid(),
            Type = request.Type.Trim(),
            Status = "pending",
            Priority = request.Priority,
            CreatedByUserId = createdByUserId,
            CreatedByDisplayName = string.IsNullOrWhiteSpace(createdByDisplayName) ? null : createdByDisplayName.Trim(),
            TargetEntityType = string.IsNullOrWhiteSpace(request.TargetEntityType) ? null : request.TargetEntityType.Trim(),
            TargetEntityId = request.TargetEntityId,
            CourseId = request.CourseId,
            ParentJobId = request.ParentJobId,
            StageCode = string.IsNullOrWhiteSpace(request.StageCode) ? null : request.StageCode.Trim(),
            StageLabel = string.IsNullOrWhiteSpace(request.StageLabel) ? null : request.StageLabel.Trim(),
            StageOrder = request.StageOrder,
            InputJson = NormalizeJsonOrNull(request.InputJson),
            CreatedAtUtc = DateTime.UtcNow,
        };

        foreach (var f in request.Files ?? new())
        {
            if (string.IsNullOrWhiteSpace(f.FileKey)) continue;
            job.Files.Add(new AiJobFile
            {
                Id = Guid.NewGuid(),
                FileKey = f.FileKey.Trim(),
                OriginalName = string.IsNullOrWhiteSpace(f.OriginalName) ? null : f.OriginalName.Trim(),
                MimeType = string.IsNullOrWhiteSpace(f.MimeType) ? null : f.MimeType.Trim(),
                PublicUrl = string.IsNullOrWhiteSpace(f.PublicUrl) ? null : f.PublicUrl.Trim(),
                CreatedAtUtc = DateTime.UtcNow,
            });
        }

        _db.AiJobs.Add(job);
        await _db.SaveChangesAsync(ct);
        Console.WriteLine($"[AiJobService] enqueued jobId={job.Id} type='{job.Type}' priority={job.Priority} courseId='{job.CourseId}' targetType='{job.TargetEntityType}' targetId='{job.TargetEntityId}' files={job.Files.Count} createdBy='{job.CreatedByDisplayName}' inputJson.len={job.InputJson?.Length ?? 0}");
        _log.LogInformation("AI job enqueued: {JobId} {Type}", job.Id, job.Type);
        return MapDetails(job);
    }

    public async Task<AiJobDetailsDto> QueueGenerateAssignmentFromTextAsync(AiGenerateAssignmentFromTextRequestDto request, Guid? createdByUserId, string? createdByDisplayName, CancellationToken ct = default)
    {
        var input = await BuildGenerationInputAsync(
            requestType: "assignment_generate_from_text",
            assignmentType: request.AssignmentType,
            courseId: request.CourseId,
            prompt: request.Prompt,
            titleHint: request.TitleHint,
            difficulty: request.Difficulty,
            count: request.Count,
            notes: request.Notes,
            enableSelfCheck: request.EnableSelfCheck,
            sourceText: request.SourceText,
            file: null,
            ct: ct,
            additional: new
            {
                instructionStrictness = request.InstructionStrictness,
                userInstructionSnapshot = request.UserInstructionSnapshot,
                teachingScript = request.TeachingScript,
                chatSessionId = request.ChatSessionId,
                structuredContext = string.IsNullOrWhiteSpace(request.StructuredContextJson)
                    ? (JsonNode?)null
                    : JsonSerializer.Deserialize<JsonNode>(request.StructuredContextJson, JsonOptions),
            });

        return await EnqueueAsync(new CreateAiJobRequestDto
        {
            Type = "assignment_generate_from_text",
            TargetEntityType = "course",
            TargetEntityId = request.CourseId,
            CourseId = request.CourseId,
            Priority = request.Priority,
            InputJson = JsonSerializer.Serialize(input, JsonOptions),
        }, createdByUserId, createdByDisplayName, ct);
    }

    public async Task<AiJobDetailsDto> QueueGenerateAssignmentFromFileAsync(AiGenerateAssignmentFromFileRequestDto request, Guid? createdByUserId, string? createdByDisplayName, CancellationToken ct = default)
    {
        var input = await BuildGenerationInputAsync(
            requestType: "assignment_generate_from_file",
            assignmentType: request.AssignmentType,
            courseId: request.CourseId,
            prompt: request.Prompt,
            titleHint: request.TitleHint,
            difficulty: request.Difficulty,
            count: request.Count,
            notes: request.Notes,
            enableSelfCheck: request.EnableSelfCheck,
            sourceText: null,
            file: new { request.FileKey, request.OriginalName, request.MimeType, request.PublicUrl },
            ct: ct,
            additional: new
            {
                instructionStrictness = request.InstructionStrictness,
                userInstructionSnapshot = request.UserInstructionSnapshot,
                teachingScript = request.TeachingScript,
                chatSessionId = request.ChatSessionId,
                structuredContext = (JsonNode?)null,
            });

        return await EnqueueAsync(new CreateAiJobRequestDto
        {
            Type = "assignment_generate_from_file",
            TargetEntityType = "course",
            TargetEntityId = request.CourseId,
            CourseId = request.CourseId,
            Priority = request.Priority,
            InputJson = JsonSerializer.Serialize(input, JsonOptions),
            Files = new List<AiJobFileDto>
            {
                new()
                {
                    FileKey = request.FileKey,
                    OriginalName = request.OriginalName,
                    MimeType = request.MimeType,
                    PublicUrl = request.PublicUrl,
                }
            }
        }, createdByUserId, createdByDisplayName, ct);
    }

    public async Task<AiJobDetailsDto?> QueueAnalyzeAssignmentAsync(AiAnalyzeAssignmentRequestDto request, Guid? createdByUserId, string? createdByDisplayName, CancellationToken ct = default)
    {
        var payload = await BuildAssignmentAnalysisInputAsync(request, ct);
        if (payload == null) return null;

        var dedupeSinceUtc = DateTime.UtcNow.AddHours(-24);
        var activeJob = await _db.AiJobs.AsNoTracking()
            .Where(x => x.Type == "assignment_analyze_existing"
                && x.TargetEntityId == request.AssignmentId
                && x.CompletedAtUtc == null
                && (x.Status == "queued" || x.Status == "processing" || x.Status == "retry" || x.Status == "running"))
            .OrderByDescending(x => x.CreatedAtUtc)
            .FirstOrDefaultAsync(ct);
        if (activeJob != null)
            return MapDetails(activeJob);

        var recentReusableJob = await _db.AiJobs.AsNoTracking()
            .Where(x => x.Type == "assignment_analyze_existing"
                && x.TargetEntityId == request.AssignmentId
                && x.CreatedAtUtc >= dedupeSinceUtc
                && (
                    (x.CompletedAtUtc != null && (x.Status == "done" || x.Status == "completed"))
                    || (x.ResultJson != null && x.ResultJson != "" && x.Status != "failed" && x.Status != "error" && x.Status != "cancelled")
                ))
            .OrderByDescending(x => x.CompletedAtUtc ?? x.CreatedAtUtc)
            .FirstOrDefaultAsync(ct);
        if (recentReusableJob != null)
            return MapDetails(recentReusableJob);

        var hasFreshOverviewInsight = !request.IncludeAttempts
            && !request.IncludeStats
            && await _db.AiAssignmentInsights.AsNoTracking()
                .AnyAsync(x => x.AssignmentId == request.AssignmentId
                    && (x.Kind == AiAssignmentOverviewHelper.CourseOverviewKind || x.Kind == AiAssignmentOverviewHelper.LegacyOverviewKind)
                    && x.CreatedAtUtc >= dedupeSinceUtc, ct);
        if (hasFreshOverviewInsight)
            return null;

        return await EnqueueAsync(new CreateAiJobRequestDto
        {
            Type = "assignment_analyze_existing",
            TargetEntityType = "assignment",
            TargetEntityId = request.AssignmentId,
            CourseId = payload.Value.CourseId,
            Priority = request.Priority,
            InputJson = payload.Value.Json,
        }, createdByUserId, createdByDisplayName, ct);
    }

    public async Task<IReadOnlyList<AiJobDetailsDto>> QueueAssignmentOverviewBackfillAsync(AiBackfillAssignmentOverviewsRequestDto request, Guid? createdByUserId, string? createdByDisplayName, CancellationToken ct = default)
    {
        var limit = request.Limit <= 0 ? 200 : Math.Min(request.Limit, 1000);
        var query = _db.TaskAssignments.AsNoTracking().AsQueryable();
        if (request.CourseId.HasValue)
            query = query.Where(x => x.CourseId == request.CourseId.Value);

        if (request.OnlyMissing)
        {
            query = query.Where(x => !_db.AiAssignmentInsights.Any(i => i.AssignmentId == x.Id && (i.Kind == AiAssignmentOverviewHelper.CourseOverviewKind || i.Kind == AiAssignmentOverviewHelper.LegacyOverviewKind)));
        }

        var ids = await query
            .OrderBy(x => x.CourseId)
            .ThenBy(x => x.Sort)
            .Select(x => x.Id)
            .Take(limit)
            .ToListAsync(ct);

        var jobs = new List<AiJobDetailsDto>(ids.Count);
        foreach (var id in ids)
        {
            var job = await QueueAnalyzeAssignmentAsync(new AiAnalyzeAssignmentRequestDto
            {
                AssignmentId = id,
                IncludeStats = false,
                IncludeAttempts = false,
                Priority = request.Priority,
            }, createdByUserId, createdByDisplayName, ct);
            if (job != null)
                jobs.Add(job);
        }

        return jobs;
    }

    public async Task<AiEnsureCourseAssignmentOverviewsResultDto> EnsureCourseAssignmentOverviewsAsync(AiEnsureCourseAssignmentOverviewsRequestDto request, Guid? createdByUserId, string? createdByDisplayName, CancellationToken ct = default)
    {
        var limit = request.Limit <= 0 ? 60 : Math.Min(request.Limit, 1000);
        var totalAssignments = await _db.TaskAssignments.AsNoTracking()
            .CountAsync(x => x.CourseId == request.CourseId, ct);

        var assignmentsWithOverview = await _db.AiAssignmentInsights.AsNoTracking()
            .Where(x => x.Assignment != null
                && x.Assignment.CourseId == request.CourseId
                && (x.Kind == AiAssignmentOverviewHelper.CourseOverviewKind || x.Kind == AiAssignmentOverviewHelper.LegacyOverviewKind))
            .Select(x => x.AssignmentId)
            .Distinct()
            .CountAsync(ct);

        var query = _db.TaskAssignments.AsNoTracking()
            .Where(x => x.CourseId == request.CourseId);

        if (request.OnlyMissing)
        {
            query = query.Where(x => !_db.AiAssignmentInsights.Any(i => i.AssignmentId == x.Id
                && (i.Kind == AiAssignmentOverviewHelper.CourseOverviewKind || i.Kind == AiAssignmentOverviewHelper.LegacyOverviewKind)));
        }

        var candidateIds = await query
            .OrderBy(x => x.Sort)
            .ThenBy(x => x.CreatedAt)
            .Select(x => x.Id)
            .Take(limit)
            .ToListAsync(ct);

        var activeAssignmentIds = candidateIds.Count == 0
            ? new HashSet<Guid>()
            : (await _db.AiJobs.AsNoTracking()
                .Where(x => x.Type == "assignment_analyze_existing"
                    && x.TargetEntityId.HasValue
                    && candidateIds.Contains(x.TargetEntityId.Value)
                    && x.CompletedAtUtc == null
                    && (x.Status == "queued" || x.Status == "processing" || x.Status == "retry" || x.Status == "running"))
                .Select(x => x.TargetEntityId!.Value)
                .Distinct()
                .ToListAsync(ct))
                .ToHashSet();

        var recentlyCompletedAssignmentIds = candidateIds.Count == 0
            ? new HashSet<Guid>()
            : (await _db.AiJobs.AsNoTracking()
                .Where(x => x.Type == "assignment_analyze_existing"
                    && x.TargetEntityId.HasValue
                    && candidateIds.Contains(x.TargetEntityId.Value)
                    && x.CompletedAtUtc != null
                    && x.CompletedAtUtc >= DateTime.UtcNow.AddHours(-12)
                    && (x.Status == "done" || x.Status == "completed"))
                .Select(x => x.TargetEntityId!.Value)
                .Distinct()
                .ToListAsync(ct))
                .ToHashSet();

        var jobs = new List<AiJobDetailsDto>();
        foreach (var assignmentId in candidateIds)
        {
            if (activeAssignmentIds.Contains(assignmentId) || recentlyCompletedAssignmentIds.Contains(assignmentId))
                continue;

            var job = await QueueAnalyzeAssignmentAsync(new AiAnalyzeAssignmentRequestDto
            {
                AssignmentId = assignmentId,
                IncludeStats = false,
                IncludeAttempts = false,
                Priority = request.Priority,
            }, createdByUserId, createdByDisplayName, ct);
            if (job != null)
                jobs.Add(job);
        }

        return new AiEnsureCourseAssignmentOverviewsResultDto
        {
            CourseId = request.CourseId,
            TotalAssignments = totalAssignments,
            AssignmentsWithOverview = assignmentsWithOverview,
            AssignmentsMissingOverview = Math.Max(0, totalAssignments - assignmentsWithOverview),
            ConsideredAssignments = candidateIds.Count,
            QueuedJobsCount = jobs.Count,
            ActiveJobSkips = activeAssignmentIds.Count,
            AutoTriggered = request.AutoTriggered,
            TriggeredAtUtc = DateTime.UtcNow,
            Jobs = jobs,
        };
    }

    public async Task<AiJobDetailsDto?> QueueReviewSubmissionAsync(AiReviewSubmissionRequestDto request, Guid? createdByUserId, string? createdByDisplayName, CancellationToken ct = default)
    {
        var payload = await BuildSubmissionReviewInputAsync(request, ct);
        if (payload == null) return null;

        return await EnqueueAsync(new CreateAiJobRequestDto
        {
            Type = "submission_review",
            TargetEntityType = payload.Value.TargetEntityType,
            TargetEntityId = payload.Value.TargetEntityId,
            CourseId = payload.Value.CourseId,
            Priority = request.Priority,
            InputJson = payload.Value.Json,
        }, createdByUserId, createdByDisplayName, ct);
    }

    public async Task<AiJobDetailsDto?> QueueReviewUserAsync(AiReviewUserRequestDto request, Guid? createdByUserId, string? createdByDisplayName, CancellationToken ct = default)
    {
        var payload = await BuildUserRiskInputAsync(request, ct);
        if (payload == null) return null;

        return await EnqueueAsync(new CreateAiJobRequestDto
        {
            Type = "user_risk_review",
            TargetEntityType = "user",
            TargetEntityId = request.UserId,
            Priority = request.Priority,
            InputJson = payload,
        }, createdByUserId, createdByDisplayName, ct);
    }

    public async Task<IReadOnlyList<AiSubmissionReviewListItemDto>> GetSubmissionReviewsAsync(Guid? userId, Guid? assignmentId, CancellationToken ct = default)
    {
        var q = _db.AiSubmissionReviews.AsNoTracking().AsQueryable();
        if (userId != null) q = q.Where(x => x.UserId == userId);
        if (assignmentId != null) q = q.Where(x => x.AssignmentId == assignmentId);
        return await q.OrderByDescending(x => x.CreatedAtUtc)
            .Take(100)
            .Select(x => new AiSubmissionReviewListItemDto
            {
                Id = x.Id,
                JobId = x.JobId,
                UserId = x.UserId,
                AssignmentId = x.AssignmentId,
                SourceType = x.SourceType,
                SourceAttemptId = x.SourceAttemptId,
                Verdict = x.Verdict,
                Score = x.Score,
                Summary = x.Summary,
                SignalsJson = x.SignalsJson,
                CreatedAtUtc = x.CreatedAtUtc,
            })
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<AiUserRiskReportListItemDto>> GetUserRiskReportsAsync(Guid? userId, CancellationToken ct = default)
    {
        var q = _db.AiUserRiskReports.AsNoTracking().AsQueryable();
        if (userId != null) q = q.Where(x => x.UserId == userId);
        return await q.OrderByDescending(x => x.CreatedAtUtc)
            .Take(100)
            .Select(x => new AiUserRiskReportListItemDto
            {
                Id = x.Id,
                JobId = x.JobId,
                UserId = x.UserId,
                RiskLevel = x.RiskLevel,
                Score = x.Score,
                Summary = x.Summary,
                SignalsJson = x.SignalsJson,
                CreatedAtUtc = x.CreatedAtUtc,
                ExpiresAtUtc = x.ExpiresAtUtc,
            })
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<AiAssignmentInsightListItemDto>> GetAssignmentInsightsAsync(Guid? assignmentId, CancellationToken ct = default)
    {
        var q = _db.AiAssignmentInsights.AsNoTracking().AsQueryable();
        if (assignmentId != null) q = q.Where(x => x.AssignmentId == assignmentId);
        return await q.OrderByDescending(x => x.CreatedAtUtc)
            .Take(100)
            .Select(x => new AiAssignmentInsightListItemDto
            {
                Id = x.Id,
                JobId = x.JobId,
                AssignmentId = x.AssignmentId,
                Kind = x.Kind,
                Summary = x.Summary,
                SuggestionsJson = x.SuggestionsJson,
                CreatedAtUtc = x.CreatedAtUtc,
            })
            .ToListAsync(ct);
    }

    public async Task<AiWorkerPullResponseDto?> PullNextAsync(string workerId, IReadOnlyCollection<string> capabilities, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var caps = (capabilities ?? Array.Empty<string>())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim().ToLowerInvariant())
            .ToHashSet();

        Console.WriteLine($"[AiJobService] pull-next >>> workerId='{workerId}' caps='[{string.Join(",", caps)}]' utcNow={now:O}");

        var candidates = await _db.AiJobs
            .Include(x => x.Files)
            .Where(x => (x.Status == "pending" || x.Status == "retry") && (x.NextAttemptAtUtc == null || x.NextAttemptAtUtc <= now))
            .OrderByDescending(x => x.Priority)
            .ThenBy(x => x.CreatedAtUtc)
            .Take(20)
            .ToListAsync(ct);

        Console.WriteLine($"[AiJobService] pull-next found {candidates.Count} candidate(s)");
        foreach (var candidate in candidates)
        {
            var candidateType = candidate.Type?.Trim().ToLowerInvariant() ?? string.Empty;
            var capabilityMatch = CapabilityMatches(caps, candidateType);
            Console.WriteLine($"[AiJobService] candidate jobId={candidate.Id} type='{candidate.Type}' status='{candidate.Status}' priority={candidate.Priority} retryCount={candidate.RetryCount} nextAttemptAt='{candidate.NextAttemptAtUtc:O}' files={candidate.Files.Count} capabilityMatch={capabilityMatch} workerId='{candidate.WorkerId}' startedAt='{candidate.StartedAtUtc:O}' heartbeatAt='{candidate.HeartbeatAtUtc:O}'");
        }

        var job = candidates.FirstOrDefault(x => CapabilityMatches(caps, x.Type?.Trim().ToLowerInvariant() ?? string.Empty));
        if (job == null)
        {
            Console.WriteLine($"[AiJobService] pull-next <<< no matching job for workerId='{workerId}'");
            return null;
        }

        job.Status = "running";
        job.WorkerId = workerId;
        job.StartedAtUtc ??= now;
        job.HeartbeatAtUtc = now;
        await _db.SaveChangesAsync(ct);

        Console.WriteLine($"[AiJobService] pull-next <<< picked jobId={job.Id} type='{job.Type}' status='{job.Status}' workerId='{job.WorkerId}' startedAt='{job.StartedAtUtc:O}' heartbeatAt='{job.HeartbeatAtUtc:O}'");

        return new AiWorkerPullResponseDto
        {
            Id = job.Id,
            Type = job.Type,
            Priority = job.Priority,
            ModelName = job.ModelName,
            TargetEntityType = job.TargetEntityType,
            TargetEntityId = job.TargetEntityId,
            CourseId = job.CourseId,
            ParentJobId = job.ParentJobId,
            StageCode = job.StageCode,
            StageLabel = job.StageLabel,
            StageOrder = job.StageOrder,
            InputJson = job.InputJson,
            Files = job.Files.Select(MapFile).ToList(),
        };
    }

    private static bool CapabilityMatches(IReadOnlySet<string> caps, string jobType)
    {
        if (caps == null || caps.Count == 0 || caps.Contains("*"))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(jobType))
        {
            return false;
        }

        if (caps.Contains(jobType))
        {
            return true;
        }

        if (caps.Contains("assignment") && jobType.StartsWith("assignment_"))
        {
            return true;
        }

        if (caps.Contains("generate") && (
            jobType.StartsWith("assignment_generate_")
            || jobType == "assignment_improve_existing"
            || jobType == AiFoundryJobTypes.BatchPlan
            || jobType == AiFoundryJobTypes.BatchReplan
            || jobType == AiFoundryJobTypes.BriefGenerate
            || jobType == AiFoundryJobTypes.BriefRepair
            || jobType == AiFoundryJobTypes.Repair))
        {
            return true;
        }

        if (caps.Contains("analyze") && (
            jobType == "assignment_analyze_existing"
            || jobType == "assignment_validate_draft"
            || jobType == "support_message_review"
            || jobType == "minecraft_chat_review"
            || jobType == AiFoundryJobTypes.CourseProfileBuild
            || jobType == AiFoundryJobTypes.GapAnalysis
            || jobType == AiFoundryJobTypes.ReferencePackBuild
            || jobType == AiFoundryJobTypes.BatchPublishPrepare
            || jobType == AiFoundryJobTypes.PlannerFeedback))
        {
            return true;
        }

        if (caps.Contains("review") && (
            jobType.EndsWith("_review")
            || jobType == "assignment_validate_draft"
            || jobType == AiFoundryJobTypes.StudentJourneyReview))
        {
            return true;
        }

        if (caps.Contains("risk") && (
            jobType == "user_risk_review"
            || jobType == AiFoundryJobTypes.RuntimeReview))
        {
            return true;
        }

        if (caps.Contains("chat") && jobType == AiFoundryJobTypes.ChatTurn)
        {
            return true;
        }

        return false;
    }

    public async Task<bool> HeartbeatAsync(Guid jobId, string workerId, CancellationToken ct = default)
    {
        Console.WriteLine($"[AiJobService] heartbeat >>> jobId={jobId} workerId='{workerId}'");
        var job = await _db.AiJobs.FirstOrDefaultAsync(x => x.Id == jobId, ct);
        if (job == null)
        {
            Console.WriteLine($"[AiJobService] heartbeat <<< jobId={jobId} not found");
            return false;
        }
        if (!string.Equals(job.WorkerId, workerId, StringComparison.Ordinal))
        {
            Console.WriteLine($"[AiJobService] heartbeat <<< worker mismatch jobId={jobId} expectedWorkerId='{job.WorkerId}' actualWorkerId='{workerId}'");
            return false;
        }
        job.HeartbeatAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        Console.WriteLine($"[AiJobService] heartbeat <<< updated jobId={jobId} heartbeatAt='{job.HeartbeatAtUtc:O}'");
        return true;
    }

    public async Task<bool> CompleteAsync(Guid jobId, AiWorkerCompleteRequestDto request, CancellationToken ct = default)
    {
        Console.WriteLine($"[AiJobService] complete >>> jobId={jobId} workerId='{request.WorkerId}' model='{request.ModelName}' resultJson.len={request.ResultJson?.Length ?? 0}");
        var job = await _db.AiJobs.Include(x => x.Files).FirstOrDefaultAsync(x => x.Id == jobId, ct);
        if (job == null)
        {
            Console.WriteLine($"[AiJobService] complete <<< jobId={jobId} not found");
            return false;
        }
        if (!string.Equals(job.WorkerId, request.WorkerId, StringComparison.Ordinal))
        {
            Console.WriteLine($"[AiJobService] complete <<< worker mismatch jobId={jobId} expectedWorkerId='{job.WorkerId}' actualWorkerId='{request.WorkerId}'");
            return false;
        }

        ConsoleFoundry("complete-loaded", job, $"incomingWorker='{request.WorkerId}' incomingModel='{request.ModelName}' incomingResultLen={request.ResultJson?.Length ?? 0}");

        job.Status = "done";
        job.CompletedAtUtc = DateTime.UtcNow;
        job.HeartbeatAtUtc = job.CompletedAtUtc;
        job.ModelName = string.IsNullOrWhiteSpace(request.ModelName) ? job.ModelName : request.ModelName.Trim();
        job.ResultJson = NormalizeJsonOrNull(request.ResultJson) ?? request.ResultJson;
        job.ErrorText = null;

        ConsoleFoundry("complete-before-artifacts", job, $"normalizedResultLen={job.ResultJson?.Length ?? 0} resultPreview='{PreviewForConsole(job.ResultJson, 220)}'");
        try
        {
            await PersistWorkerTelemetryArtifactsAsync(job, request.TelemetryJson, ct);
            await PersistDerivedArtifactsAsync(job, ct);
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsDraftSaveConflict(ex))
        {
            Console.WriteLine($"[AiJobService] complete draft-conflict detected jobId={jobId}: {ex.Message}");
            await ResolveDraftSaveConflictAndRetryAsync(jobId, request, ct);
            job = await _db.AiJobs.Include(x => x.Files).FirstOrDefaultAsync(x => x.Id == jobId, ct) ?? job;
        }
        ConsoleFoundry("complete-after-artifacts", job);
        await SyncFoundryProgressAfterCompletionAsync(job, ct);
        await _db.SaveChangesAsync(ct);
        ConsoleFoundry("complete-after-sync", job);
        Console.WriteLine($"[AiJobService] complete <<< saved jobId={jobId} status='{job.Status}' completedAt='{job.CompletedAtUtc:O}' model='{job.ModelName}'");
        return true;
    }

    private async Task PersistWorkerTelemetryArtifactsAsync(AiJob job, string? telemetryJson, CancellationToken ct)
    {
        telemetryJson = NormalizeJsonOrNull(telemetryJson) ?? telemetryJson;
        if (string.IsNullOrWhiteSpace(telemetryJson))
        {
            await PersistInputTelemetryArtifactsAsync(job, ct);
            return;
        }

        var existing = await _db.AiArtifacts.FirstOrDefaultAsync(x => x.JobId == job.Id && x.ArtifactType == "worker-telemetry", ct);
        if (existing == null)
        {
            existing = new AiArtifact
            {
                Id = Guid.NewGuid(),
                JobId = job.Id,
                DraftId = job.TargetEntityType != null && job.TargetEntityType.Equals("ai-draft", StringComparison.OrdinalIgnoreCase) ? job.TargetEntityId : null,
                ArtifactType = "worker-telemetry",
                StageCode = job.StageCode,
                Status = job.Status,
                ModelName = job.ModelName,
                PayloadJson = telemetryJson,
                CreatedAtUtc = DateTime.UtcNow,
            };
            _db.AiArtifacts.Add(existing);
        }
        else
        {
            existing.StageCode = job.StageCode;
            existing.Status = job.Status;
            existing.ModelName = job.ModelName;
            existing.PayloadJson = telemetryJson;
        }

        await PersistInputTelemetryArtifactsAsync(job, ct);
    }

    private async Task PersistInputTelemetryArtifactsAsync(AiJob job, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(job.InputJson))
        {
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(job.InputJson);
            var root = doc.RootElement;
            if (root.TryGetProperty("selectionTelemetry", out var selection) && selection.ValueKind == JsonValueKind.Object)
            {
                var existingSelection = await _db.AiArtifacts.FirstOrDefaultAsync(x => x.JobId == job.Id && x.ArtifactType == "selection-telemetry", ct);
                if (existingSelection == null)
                {
                    _db.AiArtifacts.Add(new AiArtifact
                    {
                        Id = Guid.NewGuid(),
                        JobId = job.Id,
                        DraftId = job.TargetEntityType != null && job.TargetEntityType.Equals("ai-draft", StringComparison.OrdinalIgnoreCase) ? job.TargetEntityId : null,
                        ArtifactType = "selection-telemetry",
                        StageCode = job.StageCode,
                        Status = job.Status,
                        ModelName = job.ModelName,
                        PayloadJson = selection.GetRawText(),
                        CreatedAtUtc = DateTime.UtcNow,
                    });
                }
                else
                {
                    existingSelection.PayloadJson = selection.GetRawText();
                    existingSelection.StageCode = job.StageCode;
                    existingSelection.Status = job.Status;
                    existingSelection.ModelName = job.ModelName;
                }
            }

            if (root.TryGetProperty("anchorContext", out var anchor) && anchor.ValueKind == JsonValueKind.Object && anchor.TryGetProperty("duplicateClustersPreview", out var clusters) && clusters.ValueKind == JsonValueKind.Array)
            {
                var payload = JsonSerializer.Serialize(new
                {
                    clusterCount = clusters.GetArrayLength(),
                    clusters = clusters,
                }, JsonOptions);
                var existingClusters = await _db.AiArtifacts.FirstOrDefaultAsync(x => x.JobId == job.Id && x.ArtifactType == "duplicate-clusters", ct);
                if (existingClusters == null)
                {
                    _db.AiArtifacts.Add(new AiArtifact
                    {
                        Id = Guid.NewGuid(),
                        JobId = job.Id,
                        DraftId = job.TargetEntityType != null && job.TargetEntityType.Equals("ai-draft", StringComparison.OrdinalIgnoreCase) ? job.TargetEntityId : null,
                        ArtifactType = "duplicate-clusters",
                        StageCode = job.StageCode,
                        Status = job.Status,
                        ModelName = job.ModelName,
                        PayloadJson = payload,
                        CreatedAtUtc = DateTime.UtcNow,
                    });
                }
                else
                {
                    existingClusters.PayloadJson = payload;
                    existingClusters.StageCode = job.StageCode;
                    existingClusters.Status = job.Status;
                    existingClusters.ModelName = job.ModelName;
                }
            }
        }
        catch (JsonException)
        {
        }
    }

    private static bool IsDraftSaveConflict(DbUpdateException ex)
    {
        var text = ex.ToString();
        return text.Contains("AiGeneratedAssignmentDrafts", StringComparison.OrdinalIgnoreCase)
            && (text.Contains("BatchItemId", StringComparison.OrdinalIgnoreCase)
                || text.Contains("JobId", StringComparison.OrdinalIgnoreCase)
                || text.Contains("duplicate key", StringComparison.OrdinalIgnoreCase)
                || text.Contains("unique constraint", StringComparison.OrdinalIgnoreCase));
    }

    private async Task ResolveDraftSaveConflictAndRetryAsync(Guid jobId, AiWorkerCompleteRequestDto request, CancellationToken ct)
    {
        _db.ChangeTracker.Clear();
        var job = await _db.AiJobs.Include(x => x.Files).FirstOrDefaultAsync(x => x.Id == jobId, ct);
        if (job == null)
        {
            return;
        }

        job.Status = "done";
        job.CompletedAtUtc = DateTime.UtcNow;
        job.HeartbeatAtUtc = job.CompletedAtUtc;
        job.ModelName = string.IsNullOrWhiteSpace(request.ModelName) ? job.ModelName : request.ModelName.Trim();
        job.ResultJson = NormalizeJsonOrNull(request.ResultJson) ?? request.ResultJson;
        job.ErrorText = null;

        await PersistDerivedArtifactsAsync(job, ct);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<bool> FailAsync(Guid jobId, AiWorkerFailRequestDto request, CancellationToken ct = default)
    {
        Console.WriteLine($"[AiJobService] fail >>> jobId={jobId} workerId='{request.WorkerId}' retryable={request.Retryable} retryDelaySeconds={request.RetryDelaySeconds} errorText='{request.ErrorText}'");
        var job = await _db.AiJobs.FirstOrDefaultAsync(x => x.Id == jobId, ct);
        if (job == null)
        {
            Console.WriteLine($"[AiJobService] fail <<< jobId={jobId} not found");
            return false;
        }
        if (!string.Equals(job.WorkerId, request.WorkerId, StringComparison.Ordinal))
        {
            Console.WriteLine($"[AiJobService] fail <<< worker mismatch jobId={jobId} expectedWorkerId='{job.WorkerId}' actualWorkerId='{request.WorkerId}'");
            return false;
        }

        job.ErrorText = request.ErrorText;
        job.HeartbeatAtUtc = DateTime.UtcNow;
        if (request.Retryable)
        {
            job.Status = "retry";
            job.RetryCount += 1;
            job.NextAttemptAtUtc = DateTime.UtcNow.AddSeconds(Math.Clamp(request.RetryDelaySeconds, 5, 3600));
            job.WorkerId = null;
            Console.WriteLine($"[AiJobService] fail retry scheduled jobId={jobId} retryCount={job.RetryCount} nextAttemptAt='{job.NextAttemptAtUtc:O}'");
        }
        else
        {
            job.Status = "failed";
            job.CompletedAtUtc = DateTime.UtcNow;
            Console.WriteLine($"[AiJobService] fail terminal jobId={jobId} completedAt='{job.CompletedAtUtc:O}'");
        }

        await _db.SaveChangesAsync(ct);
        Console.WriteLine($"[AiJobService] fail <<< saved jobId={jobId} status='{job.Status}' workerId='{job.WorkerId}'");
        return true;
    }

    public async Task<AiJobDetailsDto?> QueueValidateDraftAsync(Guid draftId, ValidateAiDraftRequestDto request, Guid? createdByUserId, string? createdByDisplayName, CancellationToken ct = default)
    {
        var draft = await _db.AiGeneratedAssignmentDrafts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == draftId, ct);
        if (draft == null) return null;

        var input = new
        {
            requestType = "assignment_validate_draft",
            draftId = draft.Id,
            courseId = draft.CourseId,
            assignmentType = draft.AssignmentType,
            prompt = request.Prompt,
            usePythonSelfCheck = request.UsePythonSelfCheck,
            draft = JsonSerializer.Deserialize<object>(draft.DraftJson ?? "{}"),
            targetSchema = BuildTargetSchema(draft.AssignmentType),
            referenceAssignments = await BuildReferenceAssignmentsAsync(draft.CourseId, draft.AssignmentType, ct),
            validationRules = BuildQualityGates(draft.AssignmentType),
        };

        return await EnqueueAsync(new CreateAiJobRequestDto
        {
            Type = "assignment_validate_draft",
            TargetEntityType = "ai-draft",
            TargetEntityId = draft.Id,
            CourseId = draft.CourseId,
            Priority = request.Priority,
            InputJson = JsonSerializer.Serialize(input, JsonOptions),
        }, createdByUserId, createdByDisplayName, ct);
    }

    public async Task<AiJobDetailsDto?> QueueReviseDraftFromChatAsync(AiReviseDraftFromChatRequestDto request, Guid? createdByUserId, string? createdByDisplayName, CancellationToken ct = default)
    {
        var draft = await _db.AiGeneratedAssignmentDrafts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == request.DraftId, ct);
        if (draft == null) return null;

        var input = new
        {
            requestType = "assignment_repair",
            draftId = draft.Id,
            courseId = draft.CourseId,
            assignmentType = draft.AssignmentType,
            prompt = request.Prompt,
            draft = JsonSerializer.Deserialize<object>(draft.DraftJson ?? "{}"),
            targetSchema = BuildTargetSchema(draft.AssignmentType),
            referenceAssignments = await BuildReferenceAssignmentsAsync(draft.CourseId, draft.AssignmentType, ct),
            qualityGates = BuildQualityGates(draft.AssignmentType),
            instructionStrictness = request.InstructionStrictness,
            userInstructionSnapshot = request.UserInstructionSnapshot,
            teachingScript = request.TeachingScript,
            chatSessionId = request.ChatSessionId,
        };

        return await EnqueueAsync(new CreateAiJobRequestDto
        {
            Type = "assignment_repair",
            TargetEntityType = "ai-draft",
            TargetEntityId = draft.Id,
            CourseId = draft.CourseId,
            Priority = request.Priority,
            InputJson = JsonSerializer.Serialize(input, JsonOptions),
        }, createdByUserId, createdByDisplayName, ct);
    }

    public async Task<IReadOnlyList<AiGeneratedDraftDto>> GetDraftsAsync(CancellationToken ct = default)
    {
        return await _db.AiGeneratedAssignmentDrafts.AsNoTracking()
            .OrderByDescending(x => x.UpdatedAtUtc)
            .Select(x => new AiGeneratedDraftDto
            {
                Id = x.Id,
                JobId = x.JobId,
                CourseId = x.CourseId,
                BatchId = x.BatchId,
                BatchItemId = x.BatchItemId,
                AssignmentType = x.AssignmentType,
                Title = x.Title,
                DraftJson = x.DraftJson,
                Status = x.Status,
                CreatedAtUtc = x.CreatedAtUtc,
                UpdatedAtUtc = x.UpdatedAtUtc,
                ReviewedAtUtc = x.ReviewedAtUtc,
            })
            .ToListAsync(ct);
    }

    public async Task<AiGeneratedDraftDto?> GetDraftAsync(Guid id, CancellationToken ct = default)
    {
        return await _db.AiGeneratedAssignmentDrafts.AsNoTracking()
            .Where(x => x.Id == id)
            .Select(x => new AiGeneratedDraftDto
            {
                Id = x.Id,
                JobId = x.JobId,
                CourseId = x.CourseId,
                BatchId = x.BatchId,
                BatchItemId = x.BatchItemId,
                AssignmentType = x.AssignmentType,
                Title = x.Title,
                DraftJson = x.DraftJson,
                Status = x.Status,
                CreatedAtUtc = x.CreatedAtUtc,
                UpdatedAtUtc = x.UpdatedAtUtc,
                ReviewedAtUtc = x.ReviewedAtUtc,
            })
            .FirstOrDefaultAsync(ct);
    }

    public async Task<AiGeneratedDraftDto?> UpdateDraftAsync(Guid id, UpdateAiDraftRequestDto request, Guid reviewedByUserId, CancellationToken ct = default)
    {
        var draft = await _db.AiGeneratedAssignmentDrafts.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (draft == null) return null;

        var raw = string.IsNullOrWhiteSpace(request.DraftJson) ? "{}" : request.DraftJson.Trim();
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;
        var publishable = ExtractPublishableDraftRoot(root);
        var normalizedTitle = NormalizePublishedDraftTitle(ReadString(publishable, "title") ?? draft.Title ?? string.Empty, ReadString(publishable, "description"));
        var normalizedType = NormalizeDraftAssignmentType(draft.AssignmentType, publishable);

        draft.DraftJson = JsonSerializer.Serialize(JsonSerializer.Deserialize<object>(raw), JsonOptions);
        draft.Title = string.IsNullOrWhiteSpace(normalizedTitle) ? (draft.Title ?? string.Empty) : normalizedTitle;
        draft.AssignmentType = normalizedType;
        draft.Status = string.Equals(draft.Status, "published", StringComparison.OrdinalIgnoreCase) ? draft.Status : "draft";
        draft.ReviewedByUserId = reviewedByUserId;
        draft.ReviewedAtUtc = DateTime.UtcNow;
        draft.UpdatedAtUtc = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
        return await GetDraftAsync(id, ct);
    }

    public async Task<bool> ReviewDraftAsync(Guid id, Guid reviewedByUserId, string action, CancellationToken ct = default)
    {
        var draft = await _db.AiGeneratedAssignmentDrafts.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (draft == null) return false;
        var normalized = (action ?? string.Empty).Trim().ToLowerInvariant();
        draft.Status = normalized switch
        {
            "approve" or "approved" => "approved",
            "reject" or "rejected" => "rejected",
            _ => "reviewed"
        };
        draft.ReviewedByUserId = reviewedByUserId;
        draft.ReviewedAtUtc = DateTime.UtcNow;
        draft.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return true;
    }

    private async Task<(Guid? CourseId, string Json)?> BuildAssignmentAnalysisInputAsync(AiAnalyzeAssignmentRequestDto request, CancellationToken ct)
    {
        var assignment = await _db.TaskAssignments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == request.AssignmentId, ct);
        if (assignment == null) return null;

        var input = new
        {
            requestType = "assignment_analyze_existing",
            prompt = request.Prompt,
            includeStats = request.IncludeStats,
            includeAttempts = request.IncludeAttempts,
            assignment = new
            {
                assignment.Id,
                assignment.CourseId,
                assignment.Title,
                assignment.Description,
                assignment.Type,
                assignment.Tags,
                assignment.Difficulty,
                assignment.Rating,
                assignment.AllowedLanguagesCsv,
                assignment.ImageTestSimilarityThreshold,
                hasImageReference = !string.IsNullOrWhiteSpace(assignment.ImageTestReferenceKey),
                details = await BuildAssignmentTypeDataAsync(assignment, ct),
            },
            stats = request.IncludeStats ? await BuildAssignmentStatsAsync(assignment, request.IncludeAttempts, ct) : null,
        };

        return (assignment.CourseId, JsonSerializer.Serialize(input, JsonOptions));
    }

    private async Task<object?> BuildAssignmentTypeDataAsync(taskforge.Data.Models.Entities.TaskAssignment assignment, CancellationToken ct)
    {
        var type = (assignment.Type ?? string.Empty).Trim().ToLowerInvariant();
        if (type == "test")
        {
            var settings = await _db.TaskTestSettings.AsNoTracking().FirstOrDefaultAsync(x => x.TaskAssignmentId == assignment.Id, ct);
            var questions = await _db.TaskTestQuestions.AsNoTracking().Where(x => x.TaskAssignmentId == assignment.Id)
                .OrderBy(x => x.Order)
                .Select(x => new { x.Id, x.Order, x.Type, x.Prompt, x.DataJson })
                .ToListAsync(ct);
            return new
            {
                settings = settings == null ? null : new { settings.MaxAttempts, settings.PassPercent, settings.ShuffleQuestions, settings.ShuffleAnswers, settings.AllowReview, settings.AttemptTimeLimitsJson },
                questions,
            };
        }

        if (type == "math")
        {
            var settings = await _db.TaskMathSettings.AsNoTracking().FirstOrDefaultAsync(x => x.TaskAssignmentId == assignment.Id, ct);
            var blocks = await _db.TaskMathBlocks.AsNoTracking().Where(x => x.TaskAssignmentId == assignment.Id)
                .OrderBy(x => x.Order)
                .Select(x => new { x.Id, x.Order, x.Kind, x.Prompt, x.PromptContentJson, x.DataJson, x.Score, x.IsRequired })
                .ToListAsync(ct);
            return new
            {
                settings = settings == null ? null : new { settings.MaxAttempts, settings.PassPercent, settings.ShuffleBlocks, settings.AllowReview, settings.AttemptTimeLimitsJson },
                blocks,
            };
        }

        if (type == "image-test")
        {
            return new
            {
                assignment.ImageTestSimilarityThreshold,
                hasReferenceImage = !string.IsNullOrWhiteSpace(assignment.ImageTestReferenceKey),
                assignment.AllowedLanguagesCsv,
            };
        }

        var publicCases = await _db.TaskTestCases.AsNoTracking().Where(x => x.TaskAssignmentId == assignment.Id && !x.IsHidden)
            .Select(x => new { x.Input, x.ExpectedOutput })
            .Take(10)
            .ToListAsync(ct);
        var hiddenCount = await _db.TaskTestCases.AsNoTracking().CountAsync(x => x.TaskAssignmentId == assignment.Id && x.IsHidden, ct);
        return new
        {
            assignment.AllowedLanguagesCsv,
            publicCases,
            hiddenTestsCount = hiddenCount,
        };
    }

    private async Task<object> BuildAssignmentStatsAsync(taskforge.Data.Models.Entities.TaskAssignment assignment, bool includeAttempts, CancellationToken ct)
    {
        var type = (assignment.Type ?? string.Empty).Trim().ToLowerInvariant();
        if (type == "test")
        {
            var attempts = _db.UserTaskTestAttempts.AsNoTracking().Where(x => x.TaskAssignmentId == assignment.Id);
            var total = await attempts.CountAsync(ct);
            var passed = await attempts.CountAsync(x => x.Passed, ct);
            var recent = includeAttempts
                ? await attempts.OrderByDescending(x => x.SubmittedAt ?? x.UpdatedAt).Take(10)
                    .Select(x => new { x.Id, x.UserId, x.AttemptNumber, x.ScorePercent, x.Passed, x.TimeExpired, x.SubmittedAt })
                    .ToListAsync(ct)
                : null;
            return new { totalAttempts = total, passedAttempts = passed, passRate = total > 0 ? Math.Round((double)passed / total, 4) : 0d, recentAttempts = recent };
        }

        if (type == "math")
        {
            var attempts = _db.UserTaskMathAttempts.AsNoTracking().Where(x => x.TaskAssignmentId == assignment.Id);
            var total = await attempts.CountAsync(ct);
            var passed = await attempts.CountAsync(x => x.Passed, ct);
            var recent = includeAttempts
                ? await attempts.OrderByDescending(x => x.SubmittedAt ?? x.UpdatedAt).Take(10)
                    .Select(x => new { x.Id, x.UserId, x.AttemptNumber, x.TotalScore, x.EarnedScore, x.ScorePercent, x.Passed, x.TimeExpired, x.SubmittedAt })
                    .ToListAsync(ct)
                : null;
            return new { totalAttempts = total, passedAttempts = passed, passRate = total > 0 ? Math.Round((double)passed / total, 4) : 0d, recentAttempts = recent };
        }

        if (type == "image-test")
        {
            var attempts = _db.UserImageTaskSolutions.AsNoTracking().Where(x => x.TaskAssignmentId == assignment.Id && !x.IsTrial);
            var total = await attempts.CountAsync(ct);
            var passed = await attempts.CountAsync(x => x.Passed == true, ct);
            var recent = includeAttempts
                ? await attempts.OrderByDescending(x => x.CreatedAtUtc).Take(10)
                    .Select(x => new { x.Id, x.UserId, x.Kind, x.Language, x.SimilarityPercent, x.ThresholdPercent, x.Passed, x.CreatedAtUtc })
                    .ToListAsync(ct)
                : null;
            return new { totalAttempts = total, passedAttempts = passed, passRate = total > 0 ? Math.Round((double)passed / total, 4) : 0d, recentAttempts = recent };
        }

        {
            var attempts = _db.UserTaskSolutions.AsNoTracking().Where(x => x.TaskAssignmentId == assignment.Id);
            var total = await attempts.CountAsync(ct);
            var passed = await attempts.CountAsync(x => x.PassedAllTests, ct);
            var recent = includeAttempts
                ? await attempts.OrderByDescending(x => x.SubmittedAt).Take(10)
                    .Select(x => new { x.Id, x.UserId, x.Language, x.PassedAllTests, x.PassedCount, x.FailedCount, x.SubmittedAt })
                    .ToListAsync(ct)
                : null;
            return new { totalAttempts = total, passedAttempts = passed, passRate = total > 0 ? Math.Round((double)passed / total, 4) : 0d, recentAttempts = recent };
        }
    }

    private async Task<(string TargetEntityType, Guid? TargetEntityId, Guid? CourseId, string Json)?> BuildSubmissionReviewInputAsync(AiReviewSubmissionRequestDto request, CancellationToken ct)
    {
        var normalized = (request.SourceType ?? string.Empty).Trim().ToLowerInvariant();
        if (normalized is "code" or "code-test" or "solution")
        {
            var item = await _db.UserTaskSolutions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == request.SourceAttemptId, ct);
            if (item == null) return null;
            var assignment = await _db.TaskAssignments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == item.TaskAssignmentId, ct);
            if (assignment == null) return null;
            var json = JsonSerializer.Serialize(new
            {
                requestType = "submission_review",
                prompt = request.Prompt,
                sourceType = "code",
                sourceAttemptId = item.Id,
                userId = item.UserId,
                assignmentId = assignment.Id,
                assignment = new { assignment.Id, assignment.CourseId, assignment.Title, assignment.Type, assignment.Difficulty, assignment.Rating },
                submission = new { item.Id, item.Language, item.SubmittedCode, item.PassedAllTests, item.PassedCount, item.FailedCount, item.SubmittedAt },
            }, JsonOptions);
            return ("assignment", assignment.Id, assignment.CourseId, json);
        }

        if (normalized is "test" or "test-attempt")
        {
            var item = await _db.UserTaskTestAttempts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == request.SourceAttemptId, ct);
            if (item == null) return null;
            var assignment = await _db.TaskAssignments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == item.TaskAssignmentId, ct);
            if (assignment == null) return null;
            var json = JsonSerializer.Serialize(new
            {
                requestType = "submission_review",
                prompt = request.Prompt,
                sourceType = "test",
                sourceAttemptId = item.Id,
                userId = item.UserId,
                assignmentId = assignment.Id,
                assignment = new { assignment.Id, assignment.CourseId, assignment.Title, assignment.Type, assignment.Difficulty, assignment.Rating },
                submission = new { item.Id, item.AttemptNumber, item.StartedAt, item.TimeLimitSeconds, item.TimeExpired, item.SubmittedAt, item.ScorePercent, item.Passed, item.QuestionOrderJson, item.AnswersJson },
            }, JsonOptions);
            return ("assignment", assignment.Id, assignment.CourseId, json);
        }

        if (normalized is "math" or "math-attempt")
        {
            var item = await _db.UserTaskMathAttempts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == request.SourceAttemptId, ct);
            if (item == null) return null;
            var assignment = await _db.TaskAssignments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == item.TaskAssignmentId, ct);
            if (assignment == null) return null;
            var json = JsonSerializer.Serialize(new
            {
                requestType = "submission_review",
                prompt = request.Prompt,
                sourceType = "math",
                sourceAttemptId = item.Id,
                userId = item.UserId,
                assignmentId = assignment.Id,
                assignment = new { assignment.Id, assignment.CourseId, assignment.Title, assignment.Type, assignment.Difficulty, assignment.Rating },
                submission = new { item.Id, item.AttemptNumber, item.StartedAt, item.TimeLimitSeconds, item.TimeExpired, item.SubmittedAt, item.TotalScore, item.EarnedScore, item.ScorePercent, item.Passed, item.BlockOrderJson, item.AnswersJson },
            }, JsonOptions);
            return ("assignment", assignment.Id, assignment.CourseId, json);
        }

        if (normalized is "image" or "image-test")
        {
            var item = await _db.UserImageTaskSolutions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == request.SourceAttemptId, ct);
            if (item == null) return null;
            var assignment = await _db.TaskAssignments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == item.TaskAssignmentId, ct);
            if (assignment == null) return null;
            var json = JsonSerializer.Serialize(new
            {
                requestType = "submission_review",
                prompt = request.Prompt,
                sourceType = "image",
                sourceAttemptId = item.Id,
                userId = item.UserId,
                assignmentId = assignment.Id,
                assignment = new { assignment.Id, assignment.CourseId, assignment.Title, assignment.Type, assignment.Difficulty, assignment.Rating },
                submission = new { item.Id, item.Kind, item.IsTrial, item.Language, item.SubmittedCode, item.SimilarityPercent, item.ThresholdPercent, item.Passed, item.Stdout, item.Stderr, item.RunnerError, item.CreatedAtUtc },
            }, JsonOptions);
            return ("assignment", assignment.Id, assignment.CourseId, json);
        }

        return null;
    }

    private async Task<string?> BuildUserRiskInputAsync(AiReviewUserRequestDto request, CancellationToken ct)
    {
        var user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Id == request.UserId, ct);
        if (user == null) return null;

        var codeAttempts = _db.UserTaskSolutions.AsNoTracking().Where(x => x.UserId == user.Id);
        var testAttempts = _db.UserTaskTestAttempts.AsNoTracking().Where(x => x.UserId == user.Id);
        var mathAttempts = _db.UserTaskMathAttempts.AsNoTracking().Where(x => x.UserId == user.Id);
        var imageAttempts = _db.UserImageTaskSolutions.AsNoTracking().Where(x => x.UserId == user.Id && !x.IsTrial);

        var supportSummary = request.IncludeSupport
            ? new
            {
                ticketsCount = await _db.SupportTickets.AsNoTracking().CountAsync(x => x.UserId == user.Id, ct),
                recentTickets = await _db.SupportTickets.AsNoTracking().Where(x => x.UserId == user.Id).OrderByDescending(x => x.UpdatedAt).Take(5)
                    .Select(x => new { x.Id, x.Type, x.IsClosed, x.CreatedAt, x.UpdatedAt, x.AssignedAdminId })
                    .ToListAsync(ct),
                recentMessages = await _db.SupportMessages.AsNoTracking().Where(x => x.AuthorUserId == user.Id).OrderByDescending(x => x.CreatedAt).Take(10)
                    .Select(x => new { x.Id, x.TicketId, x.Text, x.CreatedAt, x.IsFromAdmin, x.Source })
                    .ToListAsync(ct),
            }
            : null;

        var minecraftSummary = request.IncludeMinecraft
            ? new
            {
                linkedNick = user.MinecraftNick,
                linkedAtUtc = user.MinecraftLinkedAtUtc,
                recentMessages = await _db.MinecraftChatMessages.AsNoTracking().Where(x => x.UserId == user.Id).OrderByDescending(x => x.CreatedAtUtc).Take(20)
                    .Select(x => new { x.Id, x.Source, x.AuthorName, x.MinecraftNick, x.Message, x.CreatedAtUtc })
                    .ToListAsync(ct),
            }
            : null;

        var input = new
        {
            requestType = "user_risk_review",
            prompt = request.Prompt,
            user = new
            {
                user.Id,
                user.Email,
                user.FirstName,
                user.LastName,
                user.Role,
                user.CreatedAt,
                user.LastLoginAt,
                user.TelegramChatId,
                user.TelegramUsername,
                user.TelegramLinkedAtUtc,
                user.MinecraftNick,
                user.MinecraftUuid,
                user.MinecraftLinkedAtUtc,
            },
            aggregates = new
            {
                codeAttemptsCount = await codeAttempts.CountAsync(ct),
                codePassedCount = await codeAttempts.CountAsync(x => x.PassedAllTests, ct),
                testAttemptsCount = await testAttempts.CountAsync(ct),
                testPassedCount = await testAttempts.CountAsync(x => x.Passed, ct),
                mathAttemptsCount = await mathAttempts.CountAsync(ct),
                mathPassedCount = await mathAttempts.CountAsync(x => x.Passed, ct),
                imageAttemptsCount = await imageAttempts.CountAsync(ct),
                imagePassedCount = await imageAttempts.CountAsync(x => x.Passed == true, ct),
            },
            recentAttempts = request.IncludeRecentAttempts ? new
            {
                code = await codeAttempts.OrderByDescending(x => x.SubmittedAt).Take(8).Select(x => new { x.Id, x.TaskAssignmentId, x.Language, x.PassedAllTests, x.PassedCount, x.FailedCount, x.SubmittedAt }).ToListAsync(ct),
                test = await testAttempts.OrderByDescending(x => x.SubmittedAt ?? x.UpdatedAt).Take(8).Select(x => new { x.Id, x.TaskAssignmentId, x.AttemptNumber, x.ScorePercent, x.Passed, x.TimeExpired, x.SubmittedAt }).ToListAsync(ct),
                math = await mathAttempts.OrderByDescending(x => x.SubmittedAt ?? x.UpdatedAt).Take(8).Select(x => new { x.Id, x.TaskAssignmentId, x.AttemptNumber, x.TotalScore, x.EarnedScore, x.ScorePercent, x.Passed, x.TimeExpired, x.SubmittedAt }).ToListAsync(ct),
                image = await imageAttempts.OrderByDescending(x => x.CreatedAtUtc).Take(8).Select(x => new { x.Id, x.TaskAssignmentId, x.Kind, x.Language, x.SimilarityPercent, x.ThresholdPercent, x.Passed, x.CreatedAtUtc }).ToListAsync(ct),
            } : null,
            support = supportSummary,
            minecraft = minecraftSummary,
        };

        return JsonSerializer.Serialize(input, JsonOptions);
    }

    private async Task PersistDerivedArtifactsAsync(AiJob job, CancellationToken ct)
    {
        Console.WriteLine($"[AiJobService] persist-artifacts >>> jobId={job.Id} type='{job.Type}' resultJson.len={job.ResultJson?.Length ?? 0}");
        if (string.IsNullOrWhiteSpace(job.ResultJson))
        {
            Console.WriteLine($"[AiJobService] persist-artifacts <<< skipped empty result for jobId={job.Id}");
            return;
        }
        JsonDocument? doc = null;
        try
        {
            doc = JsonDocument.Parse(job.ResultJson);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AiJobService] persist-artifacts <<< invalid json for jobId={job.Id}: {ex.Message}");
            return;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (job.Type.StartsWith("assignment_generate", StringComparison.OrdinalIgnoreCase))
            {
                if (TryBuildDraft(job, root, out var draft))
                {
                    Console.WriteLine($"[AiJobService] persist-artifacts draft built jobId={job.Id} draftId={draft.Id} assignmentType='{draft.AssignmentType}' title='{draft.Title}' status='{draft.Status}' courseId='{draft.CourseId}'");
                    var existing = await _db.AiGeneratedAssignmentDrafts.FirstOrDefaultAsync(x => x.JobId == job.Id, ct);
                    if (string.Equals(job.TargetEntityType, "ai-batch-item", StringComparison.OrdinalIgnoreCase) && job.TargetEntityId != null)
                    {
                        var batchItem = await _db.AiBatchItems.AsNoTracking().FirstOrDefaultAsync(x => x.Id == job.TargetEntityId.Value, ct);
                        if (batchItem != null)
                        {
                            draft.BatchItemId = batchItem.Id;
                            draft.BatchId = batchItem.BatchId;
                        }
                    }
                    if (existing == null && draft.BatchItemId.HasValue)
                    {
                        existing = await _db.AiGeneratedAssignmentDrafts.FirstOrDefaultAsync(x => x.BatchItemId == draft.BatchItemId.Value, ct);
                    }
                    if (existing == null)
                    {
                        _db.AiGeneratedAssignmentDrafts.Add(draft);
                        Console.WriteLine($"[AiJobService] persist-artifacts draft inserted jobId={job.Id} draftId={draft.Id}");
                    }
                    else
                    {
                        existing.AssignmentType = draft.AssignmentType;
                        existing.Title = draft.Title;
                        existing.DraftJson = draft.DraftJson;
                        existing.Status = draft.Status;
                        existing.CourseId = draft.CourseId;
                        existing.BatchId = draft.BatchId;
                        existing.BatchItemId = draft.BatchItemId;
                        existing.UpdatedAtUtc = DateTime.UtcNow;
                        Console.WriteLine($"[AiJobService] persist-artifacts draft updated jobId={job.Id} existingDraftId={existing.Id} status='{existing.Status}'");
                    }
                }
                else
                {
                    Console.WriteLine($"[AiJobService] persist-artifacts draft build failed jobId={job.Id}");
                }
            }

            if (job.Type.Equals("submission_review", StringComparison.OrdinalIgnoreCase))
            {
                var summary = root.TryGetProperty("summary", out var s) ? s.GetString() : null;
                if (!string.IsNullOrWhiteSpace(summary))
                {
                    Console.WriteLine($"[AiJobService] persist-artifacts submission-review add jobId={job.Id} assignmentId='{ExtractGuid(root, "assignmentId") ?? job.TargetEntityId}' summary.len={summary?.Length ?? 0}");
                    _db.AiSubmissionReviews.Add(new AiSubmissionReview
                    {
                        Id = Guid.NewGuid(),
                        JobId = job.Id,
                        UserId = ExtractGuid(root, "userId"),
                        AssignmentId = ExtractGuid(root, "assignmentId") ?? job.TargetEntityId,
                        SourceType = root.TryGetProperty("sourceType", out var st) ? st.GetString() : job.TargetEntityType,
                        SourceAttemptId = ExtractGuid(root, "sourceAttemptId"),
                        Verdict = root.TryGetProperty("verdict", out var v) ? (v.GetString() ?? "needs-review") : "needs-review",
                        Score = root.TryGetProperty("score", out var sc) && sc.TryGetDouble(out var dbl) ? dbl : null,
                        Summary = summary,
                        SignalsJson = root.TryGetProperty("signals", out var sig) ? sig.GetRawText() : null,
                        CreatedAtUtc = DateTime.UtcNow,
                    });
                }
            }

            if (job.Type.Equals("user_risk_review", StringComparison.OrdinalIgnoreCase))
            {
                var userId = ExtractGuid(root, "userId") ?? job.TargetEntityId;
                var summary = root.TryGetProperty("summary", out var s) ? s.GetString() : null;
                if (userId != null && !string.IsNullOrWhiteSpace(summary))
                {
                    Console.WriteLine($"[AiJobService] persist-artifacts user-risk add jobId={job.Id} userId='{userId}' summary.len={summary?.Length ?? 0}");
                    _db.AiUserRiskReports.Add(new AiUserRiskReport
                    {
                        Id = Guid.NewGuid(),
                        JobId = job.Id,
                        UserId = userId.Value,
                        RiskLevel = root.TryGetProperty("riskLevel", out var rl) ? (rl.GetString() ?? "low") : "low",
                        Score = root.TryGetProperty("score", out var sc) && sc.TryGetDouble(out var dbl) ? dbl : 0,
                        Summary = summary,
                        SignalsJson = root.TryGetProperty("signals", out var sig) ? sig.GetRawText() : null,
                        CreatedAtUtc = DateTime.UtcNow,
                    });
                }
            }

            if (job.Type.Equals("assignment_validate_draft", StringComparison.OrdinalIgnoreCase))
            {
                var draftId = ExtractGuid(root, "draftId") ?? job.TargetEntityId;
                if (draftId != null)
                {
                    var existingDraft = await _db.AiGeneratedAssignmentDrafts.FirstOrDefaultAsync(x => x.Id == draftId.Value, ct);
                    if (existingDraft != null)
                    {
                        existingDraft.DraftJson = MergeDraftValidation(existingDraft.DraftJson, root);
                        existingDraft.Status = MapDraftStatusFromValidationRoot(root, existingDraft.Status);
                        existingDraft.UpdatedAtUtc = DateTime.UtcNow;
                        Console.WriteLine($"[AiJobService] persist-artifacts validation merged jobId={job.Id} draftId={existingDraft.Id} status='{existingDraft.Status}'");
                    }
                    else
                    {
                        Console.WriteLine($"[AiJobService] persist-artifacts validation draft not found jobId={job.Id} targetDraftId='{draftId}'");
                    }
                }
            }

            if (job.Type.Equals("assignment_repair", StringComparison.OrdinalIgnoreCase))
            {
                var draftId = ExtractGuid(root, "draftId") ?? job.TargetEntityId;
                if (draftId != null && root.TryGetProperty("draft", out var repairedDraftNode) && repairedDraftNode.ValueKind == JsonValueKind.Object)
                {
                    var existingDraft = await _db.AiGeneratedAssignmentDrafts.FirstOrDefaultAsync(x => x.Id == draftId.Value, ct);
                    if (existingDraft != null)
                    {
                        if (repairedDraftNode.TryGetProperty("title", out var titleNode) && titleNode.ValueKind == JsonValueKind.String)
                        {
                            existingDraft.Title = (titleNode.GetString() ?? existingDraft.Title).Trim();
                        }
                        existingDraft.AssignmentType = repairedDraftNode.TryGetProperty("assignmentType", out var typeNode) && typeNode.ValueKind == JsonValueKind.String
                            ? (typeNode.GetString() ?? existingDraft.AssignmentType).Trim()
                            : existingDraft.AssignmentType;
                        existingDraft.DraftJson = JsonSerializer.Serialize(repairedDraftNode, JsonOptions);
                        existingDraft.Status = MapDraftStatusFromDraftNode(repairedDraftNode);
                        existingDraft.UpdatedAtUtc = DateTime.UtcNow;
                        Console.WriteLine($"[AiJobService] persist-artifacts repair merged jobId={job.Id} draftId={existingDraft.Id} status='{existingDraft.Status}'");
                    }
                }
            }

            if (job.Type.Equals("assignment_analyze_existing", StringComparison.OrdinalIgnoreCase))
            {
                var assignmentId = ExtractGuid(root, "assignmentId") ?? job.TargetEntityId;
                var kind = root.TryGetProperty("kind", out var k) ? (k.GetString() ?? AiAssignmentOverviewHelper.CourseOverviewKind) : AiAssignmentOverviewHelper.CourseOverviewKind;
                var assignmentTitle = root.TryGetProperty("title", out var titleNode) && titleNode.ValueKind == JsonValueKind.String
                    ? titleNode.GetString()
                    : null;
                var summary = root.TryGetProperty("summary", out var s) ? s.GetString() : null;
                summary = string.IsNullOrWhiteSpace(summary)
                    ? AiAssignmentOverviewHelper.BuildFallbackSummary(root, assignmentTitle)
                    : summary;
                var storedJson = AiAssignmentOverviewHelper.BuildStoredPayloadJson(root);

                if (assignmentId != null && (!string.IsNullOrWhiteSpace(summary) || !string.IsNullOrWhiteSpace(storedJson)))
                {
                    summary ??= "AI overview сформирован автоматически.";
                    Console.WriteLine($"[AiJobService] persist-artifacts assignment-insight add jobId={job.Id} assignmentId='{assignmentId}' summary.len={summary?.Length ?? 0}");

                    if (AiAssignmentOverviewHelper.IsOverviewKind(kind))
                    {
                        var existing = await _db.AiAssignmentInsights.FirstOrDefaultAsync(x => x.AssignmentId == assignmentId.Value && x.Kind == AiAssignmentOverviewHelper.CourseOverviewKind, ct);
                        if (existing != null)
                        {
                            existing.JobId = job.Id;
                            existing.Summary = summary!;
                            existing.SuggestionsJson = storedJson;
                            existing.CreatedAtUtc = DateTime.UtcNow;
                        }
                        else
                        {
                            _db.AiAssignmentInsights.Add(new AiAssignmentInsight
                            {
                                Id = Guid.NewGuid(),
                                JobId = job.Id,
                                AssignmentId = assignmentId.Value,
                                Kind = AiAssignmentOverviewHelper.CourseOverviewKind,
                                Summary = summary!,
                                SuggestionsJson = storedJson,
                                CreatedAtUtc = DateTime.UtcNow,
                            });
                        }
                    }
                    else
                    {
                        _db.AiAssignmentInsights.Add(new AiAssignmentInsight
                        {
                            Id = Guid.NewGuid(),
                            JobId = job.Id,
                            AssignmentId = assignmentId.Value,
                            Kind = kind,
                            Summary = summary!,
                            SuggestionsJson = storedJson,
                            CreatedAtUtc = DateTime.UtcNow,
                        });
                    }
                }
            }

            Console.WriteLine($"[AiJobService] persist-artifacts <<< finished jobId={job.Id} type='{job.Type}'");
        }
    }

    private static bool TryBuildDraft(AiJob job, JsonElement root, out AiGeneratedAssignmentDraft draft)
    {
        draft = new AiGeneratedAssignmentDraft();
        if (!root.TryGetProperty("draft", out var draftNode) || draftNode.ValueKind != JsonValueKind.Object)
            return false;

        var title = draftNode.TryGetProperty("title", out var t) ? t.GetString() : null;
        var assignmentType = draftNode.TryGetProperty("assignmentType", out var at) ? at.GetString() : null;
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(assignmentType))
            return false;

        draft = new AiGeneratedAssignmentDraft
        {
            Id = Guid.NewGuid(),
            JobId = job.Id,
            RequestedByUserId = job.CreatedByUserId,
            CourseId = ExtractGuid(draftNode, "courseId") ?? job.CourseId,
            BatchItemId = string.Equals(job.TargetEntityType, "ai-batch-item", StringComparison.OrdinalIgnoreCase) ? job.TargetEntityId : null,
            AssignmentType = assignmentType.Trim(),
            Title = title.Trim(),
            DraftJson = JsonSerializer.Serialize(draftNode, JsonOptions),
            Status = MapDraftStatusFromDraftNode(draftNode),
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
        };
        return true;
    }


    private static string MapDraftStatusFromDraftNode(JsonElement draftNode)
    {
        if (IsFallbackDraft(draftNode))
            return "fallback-review";

        var selfCheckStatus = ExtractSelfCheckStatus(draftNode);
        return selfCheckStatus switch
        {
            "passed" => "ready",
            "failed" => "needs-fix",
            "needs-review" => "needs-review",
            _ => "draft",
        };
    }

    private static bool IsFallbackDraft(JsonElement draftNode)
    {
        var title = ReadString(draftNode, "title") ?? string.Empty;
        if (title.Trim().StartsWith("AI fallback", StringComparison.OrdinalIgnoreCase))
            return true;

        var tags = ReadString(draftNode, "tags") ?? string.Empty;
        if (tags.Contains("ai,fallback", StringComparison.OrdinalIgnoreCase) || tags.Contains("fallback", StringComparison.OrdinalIgnoreCase))
            return true;

        var metaNode = GetPropertyOrNull(draftNode, "meta");
        var generationSource = ReadString(metaNode, "generationSource") ?? ReadString(metaNode, "source");
        if (string.Equals(generationSource, "fallback", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    private static bool IsFallbackDraftJson(string? draftJson)
    {
        if (string.IsNullOrWhiteSpace(draftJson)) return false;
        try
        {
            using var doc = JsonDocument.Parse(draftJson);
            return IsFallbackDraft(doc.RootElement);
        }
        catch
        {
            return false;
        }
    }

    private static string MapDraftStatusFromValidationRoot(JsonElement validationRoot, string? fallback)
    {
        var status = validationRoot.TryGetProperty("status", out var value) && value.ValueKind == JsonValueKind.String
            ? (value.GetString() ?? string.Empty).Trim().ToLowerInvariant()
            : string.Empty;
        return status switch
        {
            "passed" => "ready",
            "failed" => "needs-fix",
            "needs-review" => "needs-review",
            _ => string.IsNullOrWhiteSpace(fallback) ? "draft" : fallback!,
        };
    }

    private static string? ExtractSelfCheckStatus(JsonElement draftNode)
    {
        if (!draftNode.TryGetProperty("meta", out var meta) || meta.ValueKind != JsonValueKind.Object) return null;
        if (!meta.TryGetProperty("selfCheck", out var selfCheck) || selfCheck.ValueKind != JsonValueKind.Object) return null;
        if (!selfCheck.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.String) return null;
        var value = status.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();
    }

    private static Guid? ExtractGuid(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var p)) return null;
        return p.ValueKind switch
        {
            JsonValueKind.String when Guid.TryParse(p.GetString(), out var g) => g,
            _ => null,
        };
    }

    private static string? NormalizeJsonOrNull(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try
        {
            using var doc = JsonDocument.Parse(value);
            return JsonSerializer.Serialize(doc.RootElement, JsonOptions);
        }
        catch
        {
            return JsonSerializer.Serialize(new { text = value.Trim() }, JsonOptions);
        }
    }

    private static AiJobDetailsDto MapDetails(AiJob job) => new()
    {
        Id = job.Id,
        Type = job.Type,
        Status = job.Status,
        Priority = job.Priority,
        ModelName = job.ModelName,
        WorkerId = job.WorkerId,
        CreatedByDisplayName = job.CreatedByDisplayName,
        TargetEntityType = job.TargetEntityType,
        TargetEntityId = job.TargetEntityId,
        CourseId = job.CourseId,
        CreatedAtUtc = job.CreatedAtUtc,
        StartedAtUtc = job.StartedAtUtc,
        CompletedAtUtc = job.CompletedAtUtc,
        ParentJobId = job.ParentJobId,
        StageCode = job.StageCode,
        StageLabel = job.StageLabel,
        StageOrder = job.StageOrder,
        HeartbeatAtUtc = job.HeartbeatAtUtc,
        NextAttemptAtUtc = job.NextAttemptAtUtc,
        RetryCount = job.RetryCount,
        InputJson = job.InputJson,
        ResultJson = job.ResultJson,
        TelemetryJson = job.Artifacts.OrderByDescending(x => x.CreatedAtUtc).FirstOrDefault(x => x.ArtifactType == "worker-telemetry")?.PayloadJson,
        ErrorText = job.ErrorText,
        Files = job.Files.Select(MapFile).ToList(),
        Artifacts = job.Artifacts.OrderBy(x => x.CreatedAtUtc).Select(MapArtifact).ToList(),
    };

    private static AiArtifactDto MapArtifact(AiArtifact x) => new()
    {
        Id = x.Id,
        JobId = x.JobId,
        DraftId = x.DraftId,
        ArtifactType = x.ArtifactType,
        StageCode = x.StageCode,
        Status = x.Status,
        PayloadJson = x.PayloadJson,
        ModelName = x.ModelName,
        CreatedAtUtc = x.CreatedAtUtc,
    };

    private static AiJobFileDto MapFile(AiJobFile f) => new()
    {
        FileKey = f.FileKey,
        OriginalName = f.OriginalName,
        MimeType = f.MimeType,
        PublicUrl = f.PublicUrl,
    };

    private async Task<object> BuildGenerationInputAsync(string requestType, string? assignmentType, Guid? courseId, string? prompt, string? titleHint, int difficulty, int count, string? notes, bool enableSelfCheck, string? sourceText, object? file, CancellationToken ct, object? referenceAssignmentsOverride = null, object? additional = null)
    {
        var normalizedAssignmentType = (assignmentType ?? string.Empty).Trim();
        var referenceAssignments = referenceAssignmentsOverride ?? await BuildReferenceAssignmentsAsync(courseId, normalizedAssignmentType, ct);
        var inferredLanguages = BuildSupportedLanguages(normalizedAssignmentType, referenceAssignments as IEnumerable<object>, prompt);
        var targetSchemaNode = JsonSerializer.SerializeToNode(BuildTargetSchema(normalizedAssignmentType), JsonOptions);
        if (normalizedAssignmentType == "code-test" && targetSchemaNode is JsonObject targetSchemaObj && inferredLanguages.Count > 0)
            targetSchemaObj["allowedLanguages"] = JsonSerializer.SerializeToNode(inferredLanguages, JsonOptions);

        JsonObject root = new JsonObject
        {
            ["requestType"] = requestType,
            ["assignmentType"] = normalizedAssignmentType,
            ["courseId"] = courseId?.ToString(),
            ["prompt"] = prompt,
            ["sourceText"] = sourceText,
            ["titleHint"] = titleHint,
            ["difficulty"] = difficulty,
            ["count"] = Math.Clamp(count, 1, 10),
            ["notes"] = notes,
            ["enableSelfCheck"] = enableSelfCheck,
            ["file"] = file == null ? null : JsonSerializer.SerializeToNode(file, JsonOptions),
            ["targetSchema"] = targetSchemaNode,
            ["qualityGates"] = JsonSerializer.SerializeToNode(BuildQualityGates(normalizedAssignmentType), JsonOptions),
            ["supportedLanguages"] = JsonSerializer.SerializeToNode(inferredLanguages, JsonOptions),
            ["allowedLanguages"] = JsonSerializer.SerializeToNode(inferredLanguages, JsonOptions),
            ["schemaVersion"] = "draft-v2",
            ["referenceAssignments"] = JsonSerializer.SerializeToNode(referenceAssignments, JsonOptions),
        };
        if (additional != null && JsonSerializer.SerializeToNode(additional, JsonOptions) is JsonNode additionalNode && additionalNode is JsonObject additionalObj)
        {
            foreach (var kv in additionalObj.ToList())
            {
                additionalObj.Remove(kv.Key);
                root[kv.Key] = kv.Value;
            }
        }
        return root;
    }

    private async Task<List<object>> BuildReferenceAssignmentsAsync(Guid? courseId, string? assignmentType, CancellationToken ct)
    {
        const int minDesired = 8;
        const int maxDesired = 24;
        var normalizedType = NormalizeDraftAssignmentType(assignmentType, default);

        var assignments = new List<taskforge.Data.Models.Entities.TaskAssignment>();
        var seen = new HashSet<Guid>();

        async Task LoadChunkAsync(IQueryable<taskforge.Data.Models.Entities.TaskAssignment> query, int take, bool preferCourseOrder = false)
        {
            if (take <= 0) return;
            var orderedQuery = preferCourseOrder
                ? query.AsNoTracking().OrderBy(x => x.Sort).ThenBy(x => x.CreatedAt)
                : query.AsNoTracking().OrderByDescending(x => x.UpdatedAt).ThenByDescending(x => x.CreatedAt);
            var chunk = await orderedQuery
                .Take(take)
                .ToListAsync(ct);
            foreach (var item in chunk)
            {
                if (seen.Add(item.Id)) assignments.Add(item);
            }
        }

        if (courseId != null)
        {
            await LoadChunkAsync(_db.TaskAssignments.Where(x => x.CourseId == courseId.Value && x.Type == normalizedType), maxDesired, preferCourseOrder: true);
            if (assignments.Count < minDesired)
                await LoadChunkAsync(_db.TaskAssignments.Where(x => x.CourseId == courseId.Value && x.Type != normalizedType), minDesired - assignments.Count, preferCourseOrder: true);
        }

        if (assignments.Count < maxDesired)
            await LoadChunkAsync(_db.TaskAssignments.Where(x => x.Type == normalizedType), maxDesired - assignments.Count);
        if (assignments.Count < maxDesired)
            await LoadChunkAsync(_db.TaskAssignments, maxDesired - assignments.Count);

        var overviewMap = await LoadLatestAssignmentOverviewMapAsync(assignments.Select(x => x.Id), ct);
        var result = new List<object>(assignments.Count);
        foreach (var assignment in assignments.Take(maxDesired))
        {
            overviewMap.TryGetValue(assignment.Id, out var overview);
            result.Add(await BuildReferenceAssignmentSummaryAsync(assignment, overview, ct));
        }

        Console.WriteLine($"[AiJobService] built reference assignments type='{normalizedType}' requestedCourseId='{courseId}' count={result.Count}");
        return result;
    }

    private async Task<object> BuildReferenceAssignmentSummaryAsync(taskforge.Data.Models.Entities.TaskAssignment assignment, AiAssignmentOverviewDto? overview, CancellationToken ct)
    {
        var description = (assignment.Description ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
        if (description.Length > 500) description = description[..500] + "...";

        var publicCases = new List<object>();
        int? hiddenTestsCount = null;
        int? blocksCount = null;
        int? questionsCount = null;

        var type = (assignment.Type ?? string.Empty).Trim().ToLowerInvariant();
        if (type == "code-test")
        {
            publicCases = await _db.TaskTestCases.AsNoTracking()
                .Where(x => x.TaskAssignmentId == assignment.Id && !x.IsHidden)
                .OrderBy(x => x.Id)
                 .Take(2)
                .Select(x => (object)new { x.Input, x.ExpectedOutput })
                .ToListAsync(ct);
            hiddenTestsCount = await _db.TaskTestCases.AsNoTracking().CountAsync(x => x.TaskAssignmentId == assignment.Id && x.IsHidden, ct);
        }
        else if (type == "math")
        {
            blocksCount = await _db.TaskMathBlocks.AsNoTracking().CountAsync(x => x.TaskAssignmentId == assignment.Id, ct);
        }
        else if (type == "test")
        {
            questionsCount = await _db.TaskTestQuestions.AsNoTracking().CountAsync(x => x.TaskAssignmentId == assignment.Id, ct);
        }

        return new
        {
            assignment.Id,
            assignment.CourseId,
            assignment.Type,
            assignment.Title,
            Description = description,
            assignment.Tags,
            assignment.Difficulty,
            assignment.Rating,
            assignment.Sort,
            assignment.AllowedLanguagesCsv,
            publicCases,
            hiddenTestsCount,
            blocksCount,
            questionsCount,
            forbiddenCalls = ParseJsonStringArray(assignment.CodeForbiddenCallsJson),
            requiredCalls = ParseJsonStringArray(assignment.CodeRequiredCallsJson),
            aiOverview = overview,
        };
    }

    private async Task<Dictionary<Guid, AiAssignmentOverviewDto>> LoadLatestAssignmentOverviewMapAsync(IEnumerable<Guid> assignmentIds, CancellationToken ct)
    {
        var ids = assignmentIds.Distinct().Where(x => x != Guid.Empty).ToList();
        var map = new Dictionary<Guid, AiAssignmentOverviewDto>();
        if (ids.Count == 0)
            return map;

        var insights = await _db.AiAssignmentInsights.AsNoTracking()
            .Where(x => ids.Contains(x.AssignmentId) && (x.Kind == AiAssignmentOverviewHelper.CourseOverviewKind || x.Kind == AiAssignmentOverviewHelper.LegacyOverviewKind))
            .OrderByDescending(x => x.CreatedAtUtc)
            .ToListAsync(ct);

        foreach (var insight in insights)
        {
            if (map.ContainsKey(insight.AssignmentId))
                continue;
            var overview = AiAssignmentOverviewHelper.ParseOverview(insight.SuggestionsJson, insight.Summary, insight.CreatedAtUtc);
            if (overview != null)
                map[insight.AssignmentId] = overview;
        }

        return map;
    }

    private static IReadOnlyList<string> BuildSupportedLanguages(string? assignmentType, IEnumerable<object>? referenceAssignments = null, string? prompt = null)
    {
        var normalized = NormalizeDraftAssignmentType(assignmentType, default);
        if (normalized == "image-test") return new[] { "python", "pascal", "cpp" };
        if (normalized != "code-test") return Array.Empty<string>();

        var promptHint = InferLanguageHintsFromText(prompt).ToList();
        var referenceLanguages = InferLanguagesFromReferenceAssignments(referenceAssignments).ToList();

        if (promptHint.Count == 1)
            return promptHint;
        if (referenceLanguages.Count == 1)
            return referenceLanguages;
        if (promptHint.Count > 0 && referenceLanguages.Count > 0)
        {
            var overlap = promptHint.Where(referenceLanguages.Contains).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (overlap.Count > 0)
                return overlap;
        }
        if (referenceLanguages.Count > 0)
            return referenceLanguages;

        return new[] { "cpp", "csharp", "python", "javascript", "java", "pascal" };
    }

    private static IReadOnlyList<string> InferLanguageHintsFromText(string? text)
    {
        var source = (text ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(source)) return Array.Empty<string>();
        var low = source.ToLowerInvariant();
        var langs = new List<string>();
        void Add(string value)
        {
            if (!langs.Contains(value, StringComparer.OrdinalIgnoreCase)) langs.Add(value);
        }
        if (low.Contains("c++") || low.Contains(" c plus plus") || low.Contains("cpp")) Add("cpp");
        if (low.Contains("c#") || low.Contains("csharp")) Add("csharp");
        if (low.Contains("python")) Add("python");
        if (low.Contains("javascript") || low.Contains(" js ") || low.EndsWith(" js")) Add("javascript");
        if (low.Contains(" java")) Add("java");
        if (low.Contains("pascal")) Add("pascal");
        return langs;
    }

    private static IReadOnlyList<string> InferLanguagesFromReferenceAssignments(IEnumerable<object>? referenceAssignments)
    {
        if (referenceAssignments == null) return Array.Empty<string>();
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var refsWithLangs = 0;

        foreach (var item in referenceAssignments)
        {
            if (item == null) continue;
            var csv = item.GetType().GetProperty("AllowedLanguagesCsv")?.GetValue(item)?.ToString();
            var langsForRef = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in (csv ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var lang = raw.Trim().ToLowerInvariant();
                if (lang is not ("cpp" or "csharp" or "python" or "javascript" or "java" or "pascal"))
                    continue;
                langsForRef.Add(lang);
            }
            if (langsForRef.Count == 0) continue;
            refsWithLangs++;
            foreach (var lang in langsForRef)
                counts[lang] = counts.TryGetValue(lang, out var current) ? current + 1 : 1;
        }

        if (counts.Count == 0) return Array.Empty<string>();
        if (counts.Count == 1) return counts.Keys.ToArray();

        var dominant = counts.OrderByDescending(x => x.Value).ThenBy(x => x.Key).First();
        if (dominant.Value >= Math.Max(2, (int)Math.Ceiling(refsWithLangs * 0.6)))
            return new[] { dominant.Key };

        return counts.OrderByDescending(x => x.Value).ThenBy(x => x.Key).Take(3).Select(x => x.Key).ToArray();
    }

    private static object BuildQualityGates(string? assignmentType)
    {
        var normalized = NormalizeDraftAssignmentType(assignmentType, default);
        return normalized switch
        {
            "code-test" => new
            {
                minDescriptionLength = 200,
                minPublicTests = 1,
                minHiddenTests = 0,
                minTotalTests = 1,
                preferPublicTestsMoreThanHidden = true,
                requireReferenceSolutionPython = true,
                requireCodePolicyReview = true,
                requireEdgeCases = true,
                allowedLanguagesOptionalMeansNoRestriction = false,
            },
            "test" => new
            {
                minDescriptionLength = 120,
                minQuestions = 5,
                requireDiverseQuestionTypes = true,
                requireCanonicalQuestions = true,
            },
            "math" => new
            {
                minDescriptionLength = 120,
                minBlocks = 2,
                requireAtLeastOneAnswerBlock = true,
                requireCanonicalBlocks = true,
            },
            _ => new
            {
                minDescriptionLength = 120,
            },
        };
    }

    private static List<string> ParseJsonStringArray(JsonDocument? doc)
    {
        var result = new List<string>();
        if (doc == null || doc.RootElement.ValueKind != JsonValueKind.Array) return result;
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) continue;
            var value = item.GetString();
            if (!string.IsNullOrWhiteSpace(value)) result.Add(value.Trim());
        }
        return result;
    }

    private static object BuildTargetSchema(string? assignmentType)
    {
        var normalized = (assignmentType ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "math" => new
            {
                schemaVersion = "draft-v2",
                assignmentType = "math",
                canonicalOnly = true,
                outputEnvelope = new { schemaVersion = "draft-v2", draft = "object" },
                requiredFields = new[] { "assignmentType", "title", "description", "settings", "blocks" },
                forbiddenLegacyAliases = new[] { "blockType", "title", "points", "promptContent", "answers", "correctAnswers", "items", "steps", "leftItems", "rightItems", "pairs" },
                descriptionFormat = new { allowed = new[] { "plain-text", "tiptap-json" }, minLength = 120, sections = new[] { "problem", "hints" } },
                settings = new
                {
                    maxAttempts = "int >= 1",
                    passPercent = "int 1..100",
                    shuffleBlocks = "bool",
                    allowReview = "bool",
                    attemptTimeLimitsSeconds = "int?[]"
                },
                blocks = new object[]
                {
                    new
                    {
                        kind = "info|number|expression|set|single-choice|multi-choice|order|match",
                        prompt = "string",
                        promptContentJson = "string|null",
                        score = "int >= 0",
                        isRequired = "bool",
                        options = "TaskMathOptionDto[] required for single-choice|multi-choice|match",
                        correctOptionKeys = "string[] required for single-choice|multi-choice",
                        acceptedAnswers = "string[] required for number|expression|set",
                        caseSensitive = "bool required for number|expression|set",
                        trim = "bool required for number|expression|set",
                        numericTolerance = "number optional for number",
                        orderItems = "string[] required for order",
                        matchLeftItems = "TaskMathOptionDto[] required for match",
                        matchRightItems = "TaskMathOptionDto[] required for match",
                        matchPairs = "TaskMathMatchPairDto[] required for match"
                    }
                },
                quality = new
                {
                    minBlocks = 2,
                    requireAtLeastOneAnswerBlock = true,
                    requireCanonicalBlocks = true,
                    rejectLegacyAliases = true
                },
                meta = new { selfCheck = new { status = "pending", mode = "python", summary = "Проверить структуру блоков перед публикацией." } }
            },
            "test" => new
            {
                schemaVersion = "draft-v2",
                assignmentType = "test",
                canonicalOnly = true,
                outputEnvelope = new { schemaVersion = "draft-v2", draft = "object" },
                requiredFields = new[] { "assignmentType", "title", "description", "settings", "questions" },
                forbiddenLegacyAliases = new[] { "questionType", "title", "correctKeys", "correct", "answers", "correctAnswers" },
                descriptionFormat = new { allowed = new[] { "plain-text", "tiptap-json" }, minLength = 120, sections = new[] { "problem", "instructions" } },
                settings = new
                {
                    maxAttempts = "int >= 1",
                    passPercent = "int 1..100",
                    shuffleQuestions = "bool",
                    shuffleAnswers = "bool",
                    allowReview = "bool",
                    attemptTimeLimitsSeconds = "int?[]"
                },
                questions = new object[]
                {
                    new
                    {
                        type = "single-choice|multi-choice|fill|text",
                        prompt = "string",
                        options = "TaskTestOptionDto[] required for single-choice|multi-choice",
                        correctOptionKeys = "string[] required for single-choice|multi-choice",
                        acceptedAnswers = "string[] required for fill|text",
                        caseSensitive = "bool required for fill|text",
                        trim = "bool required for fill|text"
                    }
                },
                quality = new
                {
                    minQuestions = 5,
                    mustAvoidDuplicates = true,
                    requireCanonicalQuestions = true,
                    rejectLegacyAliases = true
                },
                meta = new { selfCheck = new { status = "pending", mode = "python", summary = "Проверить вопросы и ответы перед публикацией." } }
            },
            "code-test" => new
            {
                schemaVersion = "draft-v2",
                assignmentType = "code-test",
                canonicalOnly = true,
                outputEnvelope = new { schemaVersion = "draft-v2", draft = "object" },
                requiredFields = new[] { "assignmentType", "title", "description", "publicTests", "hiddenTests", "referenceSolutionPython" },
                descriptionFormat = new { allowed = new[] { "plain-text", "tiptap-json" }, minLength = 200, sections = new[] { "problem", "input", "output", "constraints", "notes" } },
                allowedLanguages = BuildSupportedLanguages("code-test"),
                allowedLanguagesSemantics = "missing-or-empty-means-no-restriction",
                publicTests = new[] { new { input = "string", expectedOutput = "string" } },
                hiddenTests = new[] { new { input = "string", expectedOutput = "string" } },
                referenceSolutionPython = "string",
                requiredCalls = "string[] optional",
                forbiddenCalls = "string[] optional",
                quality = new
                {
                    minPublicTests = 1,
                    minHiddenTests = 0,
                    minTotalTests = 1,
                    preferPublicTestsMoreThanHidden = true,
                    requireEdgeCases = true,
                    requireDeterministicReferenceSolution = true,
                    rejectNestedCodePolicy = true
                },
                meta = new { selfCheck = new { status = "pending", mode = "python", summary = "Проверить reference solution и все тесты перед публикацией." } }
            },
            _ => new
            {
                assignmentType = assignmentType,
                requiredFields = new[] { "title", "description" },
            },
        };
    }

    public async Task<bool> DeleteDraftAsync(Guid id, CancellationToken ct = default)
    {
        var draft = await _db.AiGeneratedAssignmentDrafts.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (draft == null) return false;

        // Unlink batch-item if connected
        if (draft.BatchItemId.HasValue)
        {
            var batchItem = await _db.AiBatchItems.FirstOrDefaultAsync(x => x.Id == draft.BatchItemId.Value, ct);
            if (batchItem != null) batchItem.DraftId = null;
        }

        _db.AiGeneratedAssignmentDrafts.Remove(draft);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> DeleteJobAsync(Guid id, CancellationToken ct = default)
    {
        var job = await _db.AiJobs
            .Include(j => j.Files)
            .FirstOrDefaultAsync(x => x.Id == id, ct);
        if (job == null) return false;

        if (job.Files.Count > 0) _db.AiJobFiles.RemoveRange(job.Files);
        _db.AiJobs.Remove(job);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<int> ClearJobsAsync(string? statusFilter, CancellationToken ct = default)
    {
        IQueryable<AiJob> query = _db.AiJobs;
        if (!string.IsNullOrWhiteSpace(statusFilter))
        {
            var s = statusFilter.Trim().ToLowerInvariant();
            query = query.Where(j => j.Status.ToLower() == s);
        }
        else
        {
            // By default, clear only completed & failed — never running/pending
            query = query.Where(j => j.Status == "done" || j.Status == "failed");
        }

        var jobIds = await query.Select(j => j.Id).ToListAsync(ct);
        if (jobIds.Count == 0) return 0;

        // Remove linked files
        await _db.AiJobFiles.Where(f => jobIds.Contains(f.JobId)).ExecuteDeleteAsync(ct);
        var deleted = await _db.AiJobs.Where(j => jobIds.Contains(j.Id)).ExecuteDeleteAsync(ct);
        return deleted;
    }

    public async Task<bool> RetryJobAsync(Guid id, CancellationToken ct = default)
    {
        var job = await _db.AiJobs.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (job == null) return false;
        job.Status = "pending";
        job.ErrorText = null;
        job.WorkerId = null;
        job.StartedAtUtc = null;
        job.CompletedAtUtc = null;
        job.HeartbeatAtUtc = null;
        job.NextAttemptAtUtc = null;
        job.ResultJson = null;
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> CancelJobAsync(Guid id, CancellationToken ct = default)
    {
        var job = await _db.AiJobs.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (job == null) return false;
        if (job.Status is "done" or "failed") return false;
        job.Status = "cancelled";
        job.CompletedAtUtc = DateTime.UtcNow;
        job.ErrorText = "Cancelled by admin";
        await _db.SaveChangesAsync(ct);
        return true;
    }
}
