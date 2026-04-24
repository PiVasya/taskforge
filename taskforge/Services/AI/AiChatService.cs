using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using taskforge.Constants;
using taskforge.Data;
using taskforge.Data.Models.DTO.AI;
using taskforge.Data.Models.Entities;
using taskforge.Data.Models.Entities.AI;
using taskforge.Services.Interfaces;

namespace taskforge.Services.AI;

public sealed class AiChatService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true,
    };

    private static readonly HashSet<string> AgentLoopActionNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "analyze_course_progression",
        "inspect_course_assignments",
        "prepare_bridge_plan",
        "show_bridge_plan",
        "revise_bridge_plan",
        "advance_agent_stage",
    };

    private static readonly HashSet<string> AutonomousGroundworkActionNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "analyze_course_progression",
        "inspect_course_assignments",
        "prepare_bridge_plan",
        "advance_agent_stage",
    };

    private static readonly HashSet<string> AutonomousTerminalActionNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "show_bridge_plan",
        "revise_bridge_plan",
        "show_chat_blueprint",
        "save_chat_blueprint",
        "revise_chat_blueprint",
        "finalize_chat_blueprint",
        "drop_chat_blueprint",
        "queue_generate_bridge_batch",
        "queue_generate_batch",
        "queue_generate_from_text",
        "revise_draft_from_chat",
        "queue_generate_from_file",
        "queue_validate_draft",
        "queue_analyze_assignment",
        "queue_review_submission",
        "queue_review_user",
    };

    private const int DefaultInstructionStrictness = 55;

    private readonly ApplicationDbContext _db;
    private readonly IAiJobService _jobs;
    private readonly ILogger<AiChatService> _log;

    private sealed class ChatToolExecutionDto
    {
        public List<AiFoundryChatToolCallDto> ToolCalls { get; } = new();
        public List<AiFoundryChatToolResultDto> ToolResults { get; } = new();
        public string? AgentTrace { get; set; }
    }

    private sealed class AnchorStartDetection
    {
        public bool Detected { get; init; }
        public bool RegexMatched { get; init; }
        public List<string> MatchedMarkers { get; init; } = new();
    }

    private sealed class AnchorRoutingDiagnostics
    {
        public string Concept { get; init; } = string.Empty;
        public string Haystack { get; init; } = string.Empty;
        public string LatestText { get; init; } = string.Empty;
        public string LatestTeachingScript { get; init; } = string.Empty;
        public List<string> RecentGoalsTail { get; init; } = new();
        public bool MentionsAnchor { get; init; }
        public AnchorStartDetection LatestExplicitStart { get; init; } = new();
        public AnchorStartDetection HaystackExplicitStart { get; init; } = new();
        public bool PreAnchorDetected { get; init; }
        public string PreAnchorReason { get; init; } = "none";
        public List<string> PreAnchorMarkers { get; init; } = new();
        public bool StepByStepSeries { get; init; }
        public bool AllowsExplicitOnboarding { get; init; }
        public string Mode { get; init; } = "neutral";
    }

    private sealed class BlueprintRoutingDecision
    {
        public AnchorRoutingDiagnostics Diagnostics { get; init; } = new();
        public string HintMode { get; init; } = "none";
        public string HintConcept { get; init; } = string.Empty;
        public bool HintAgreed { get; init; }
        public bool HintProposalEvidence { get; init; }
        public bool InferredProposalOnboarding { get; init; }
        public bool LatestInstructionLooksLikeAssent { get; init; }
        public bool OverrideToOnboarding { get; init; }
        public string OverrideReason { get; init; } = "none";
        public bool ShouldAvoidExplicitAnchor { get; init; }
        public bool AllowsExplicitOnboarding { get; init; }
        public string EffectiveMode { get; init; } = "neutral";
    }

    public AiChatService(ApplicationDbContext db, IAiJobService jobs, ILogger<AiChatService> log)
    {
        _db = db;
        _jobs = jobs;
        _log = log;
    }

    public async Task<IReadOnlyList<AiFoundryChatSessionListItemDto>> GetSessionsAsync(Guid userId, CancellationToken ct = default)
    {
        var sessions = await _db.AiFoundryChatSessions
            .AsNoTracking()
            .Where(x => x.CreatedByUserId == userId)
            .OrderByDescending(x => x.UpdatedAtUtc)
            .Take(50)
            .ToListAsync(ct);

        var courseIds = sessions.Where(x => x.CourseId.HasValue).Select(x => x.CourseId!.Value).Distinct().ToList();
        var courseMap = await LoadCourseTitleMapAsync(courseIds, ct);

        return sessions.Select(x => MapListItem(x, courseMap)).ToList();
    }

    public async Task<AiFoundryChatSessionDto?> GetSessionAsync(Guid userId, Guid sessionId, CancellationToken ct = default)
    {
        var session = await _db.AiFoundryChatSessions.FirstOrDefaultAsync(x => x.Id == sessionId && x.CreatedByUserId == userId, ct);
        if (session == null)
            return null;

        await TryFinalizePendingAsync(session, ct);
        await TryAppendGenerationUpdatesAsync(session, ct);
        var parsedMessages = DeserializeMessages(session.MessagesJson);
        var memory = BuildMemory(parsedMessages, session.PlanJson);
        var courseMap = await LoadCourseTitleMapAsync(session.CourseId.HasValue ? new[] { session.CourseId.Value } : Array.Empty<Guid>(), ct);
        return MapSession(session, courseMap, parsedMessages, memory);
    }

    public async Task<AiFoundryChatTraceResponseDto?> GetSessionTraceAsync(Guid userId, Guid sessionId, CancellationToken ct = default)
    {
        var session = await _db.AiFoundryChatSessions.FirstOrDefaultAsync(x => x.Id == sessionId && x.CreatedByUserId == userId, ct);
        if (session == null)
            return null;

        await TryFinalizePendingAsync(session, ct);
        await TryAppendGenerationUpdatesAsync(session, ct);

        var messages = DeserializeMessages(session.MessagesJson);
        var memory = BuildMemory(messages, session.PlanJson);
        var linkedJobIds = messages
            .SelectMany(x => x.ToolResults ?? new List<AiFoundryChatToolResultDto>())
            .Where(x => x.JobId.HasValue)
            .Select(x => x.JobId!.Value)
            .Distinct()
            .ToList();
        var linkedBatchIds = messages
            .SelectMany(x => x.ToolResults ?? new List<AiFoundryChatToolResultDto>())
            .Where(x => x.BatchId.HasValue)
            .Select(x => x.BatchId!.Value)
            .Distinct()
            .ToList();

        var jobs = await _db.AiJobs
            .AsNoTracking()
            .Where(x => (x.TargetEntityType == "chat-session" && x.TargetEntityId == session.Id) || linkedJobIds.Contains(x.Id) || (x.TargetEntityType == "batch" && x.TargetEntityId.HasValue && linkedBatchIds.Contains(x.TargetEntityId.Value)))
            .OrderBy(x => x.CreatedAtUtc)
            .ToListAsync(ct);

        var batches = await _db.AiBatches
            .AsNoTracking()
            .Where(x => x.ChatSessionId == session.Id || linkedBatchIds.Contains(x.Id))
            .OrderBy(x => x.CreatedAtUtc)
            .ToListAsync(ct);

        return BuildTraceResponse(session, messages, memory, jobs, batches);
    }


    public async Task<AiFoundryChatMegaDebugDto?> GetSessionMegaDebugAsync(Guid userId, Guid sessionId, CancellationToken ct = default)
    {
        var session = await _db.AiFoundryChatSessions.FirstOrDefaultAsync(x => x.Id == sessionId && x.CreatedByUserId == userId, ct);
        if (session == null)
            return null;

        await TryFinalizePendingAsync(session, ct);
        await TryAppendGenerationUpdatesAsync(session, ct);

        var messages = DeserializeMessages(session.MessagesJson);
        var memory = BuildMemory(messages, session.PlanJson);
        var linkedJobIds = messages
            .SelectMany(x => x.ToolResults ?? new List<AiFoundryChatToolResultDto>())
            .Where(x => x.JobId.HasValue)
            .Select(x => x.JobId!.Value)
            .Distinct()
            .ToList();
        var linkedBatchIds = messages
            .SelectMany(x => x.ToolResults ?? new List<AiFoundryChatToolResultDto>())
            .Where(x => x.BatchId.HasValue)
            .Select(x => x.BatchId!.Value)
            .Distinct()
            .ToList();

        var jobs = await _db.AiJobs
            .AsNoTracking()
            .Where(x => (x.TargetEntityType == "chat-session" && x.TargetEntityId == session.Id) || linkedJobIds.Contains(x.Id) || (x.TargetEntityType == "batch" && x.TargetEntityId.HasValue && linkedBatchIds.Contains(x.TargetEntityId.Value)))
            .OrderBy(x => x.CreatedAtUtc)
            .ToListAsync(ct);

        var batches = await _db.AiBatches
            .AsNoTracking()
            .Where(x => x.ChatSessionId == session.Id || linkedBatchIds.Contains(x.Id))
            .OrderBy(x => x.CreatedAtUtc)
            .ToListAsync(ct);

        var draftJobIds = jobs.Select(x => x.Id)
            .Concat(jobs.Where(x => x.ParentJobId.HasValue).Select(x => x.ParentJobId!.Value))
            .Distinct()
            .ToList();

        var drafts = await _db.AiGeneratedAssignmentDrafts
            .AsNoTracking()
            .Where(x => draftJobIds.Contains(x.JobId) || (x.BatchId.HasValue && linkedBatchIds.Contains(x.BatchId.Value)))
            .OrderBy(x => x.UpdatedAtUtc)
            .ToListAsync(ct);

        var jobIds = jobs.Select(x => x.Id).Distinct().ToList();
        var telemetryArtifacts = jobIds.Count == 0
            ? new List<AiArtifact>()
            : await _db.AiArtifacts
                .AsNoTracking()
                .Where(x => jobIds.Contains(x.JobId) && x.ArtifactType == "worker-telemetry")
                .OrderByDescending(x => x.CreatedAtUtc)
                .ToListAsync(ct);
        var telemetryByJobId = telemetryArtifacts
            .GroupBy(x => x.JobId)
            .ToDictionary(x => x.Key, x => x.FirstOrDefault()?.PayloadJson);

        var trace = BuildTraceResponse(session, messages, memory, jobs, batches);
        var courseMap = await LoadCourseTitleMapAsync(session.CourseId.HasValue ? new[] { session.CourseId.Value } : Array.Empty<Guid>(), ct);

        return new AiFoundryChatMegaDebugDto
        {
            Session = MapSession(session, courseMap, messages, memory),
            Trace = trace,
            LinkedJobs = jobs.Select(x => new AiFoundryChatDebugJobDto
            {
                Id = x.Id,
                Type = x.Type,
                Status = x.Status,
                Priority = x.Priority,
                RetryCount = x.RetryCount,
                ParentJobId = x.ParentJobId,
                CourseId = x.CourseId,
                StageCode = x.StageCode,
                StageLabel = x.StageLabel,
                ErrorText = x.ErrorText,
                WorkerId = x.WorkerId,
                ModelName = x.ModelName,
                CreatedAtUtc = x.CreatedAtUtc,
                StartedAtUtc = x.StartedAtUtc,
                CompletedAtUtc = x.CompletedAtUtc,
                InputJson = x.InputJson,
                ResultJson = x.ResultJson,
                TelemetryJson = telemetryByJobId.TryGetValue(x.Id, out var telemetryJson) ? telemetryJson : null,
            }).ToList(),
            LinkedBatches = batches.Select(x => new AiFoundryChatDebugBatchDto
            {
                Id = x.Id,
                Status = x.Status,
                CurrentStage = x.CurrentStage,
                AssignmentType = x.AssignmentType,
                Mode = x.Mode,
                RequestedCount = x.RequestedCount,
                Prompt = x.Prompt,
                PlanJson = x.PlanJson,
                SummaryJson = x.SummaryJson,
                DecisionSummaryJson = x.DecisionSummaryJson,
                ReviewLedgerJson = x.ReviewLedgerJson,
                ExportManifestJson = x.ExportManifestJson,
                CreatedAtUtc = x.CreatedAtUtc,
                UpdatedAtUtc = x.UpdatedAtUtc,
            }).ToList(),
            LinkedDrafts = drafts.Select(x => new AiFoundryChatDebugDraftDto
            {
                Id = x.Id,
                JobId = x.JobId,
                BatchId = x.BatchId,
                BatchItemId = x.BatchItemId,
                CourseId = x.CourseId,
                AssignmentType = x.AssignmentType,
                Title = x.Title,
                Status = x.Status,
                DraftJson = x.DraftJson,
                CreatedAtUtc = x.CreatedAtUtc,
                UpdatedAtUtc = x.UpdatedAtUtc,
            }).ToList(),
            LoopDiagnostics = BuildLoopDiagnostics(trace),
        };
    }

    public async Task<AiFoundryChatSessionDto> CreateSessionAsync(Guid userId, AiFoundryChatCreateSessionRequestDto request, CancellationToken ct = default)
    {
        var title = string.IsNullOrWhiteSpace(request.Title) ? "Новый AI-чат" : request.Title.Trim();
        var session = new AiFoundryChatSession
        {
            Id = Guid.NewGuid(),
            CourseId = request.CourseId,
            CreatedByUserId = userId,
            Title = title,
            MessagesJson = "[]",
            PlanJson = SerializeMemory(new AiFoundryChatMemoryDto
            {
                Summary = "Новая сессия. Пока без сообщений.",
                InstructionStrictness = ClampInstructionStrictness(request.InstructionStrictness),
            }),
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
        };

        _db.AiFoundryChatSessions.Add(session);
        await _db.SaveChangesAsync(ct);

        await TryAutoEnsureCourseOverviewsAsync(session.CourseId, null, DeserializeMemory(session.PlanJson), userId, null, ct);

        var courseMap = await LoadCourseTitleMapAsync(session.CourseId.HasValue ? new[] { session.CourseId.Value } : Array.Empty<Guid>(), ct);
        return MapSession(session, courseMap, null, BuildMemory(DeserializeMessages(session.MessagesJson), session.PlanJson));
    }

    public async Task<AiFoundryChatSessionDto?> UpdateSessionAsync(Guid userId, Guid sessionId, AiFoundryChatUpdateSessionRequestDto request, CancellationToken ct = default)
    {
        var session = await _db.AiFoundryChatSessions.FirstOrDefaultAsync(x => x.Id == sessionId && x.CreatedByUserId == userId, ct);
        if (session == null)
            return null;

        if (!string.IsNullOrWhiteSpace(request.Title))
            session.Title = request.Title.Trim();

        session.CourseId = request.CourseId;
        if (request.InstructionStrictness.HasValue)
        {
            var memory = DeserializeMemory(session.PlanJson);
            memory.InstructionStrictness = ClampInstructionStrictness(request.InstructionStrictness, memory.InstructionStrictness);
            session.PlanJson = SerializeMemory(memory);
        }
        session.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        await TryAutoEnsureCourseOverviewsAsync(session.CourseId, null, DeserializeMemory(session.PlanJson), userId, null, ct);

        var courseMap = await LoadCourseTitleMapAsync(session.CourseId.HasValue ? new[] { session.CourseId.Value } : Array.Empty<Guid>(), ct);
        return MapSession(session, courseMap);
    }

    public async Task<bool> DeleteSessionAsync(Guid userId, Guid sessionId, CancellationToken ct = default)
    {
        var session = await _db.AiFoundryChatSessions.FirstOrDefaultAsync(x => x.Id == sessionId && x.CreatedByUserId == userId, ct);
        if (session == null)
            return false;

        var activeChatJobs = await _db.AiJobs
            .Where(x => x.TargetEntityType == "chat-session" && x.TargetEntityId == sessionId && x.CompletedAtUtc == null)
            .ToListAsync(ct);
        foreach (var job in activeChatJobs)
        {
            job.Status = "cancelled";
            job.CompletedAtUtc = DateTime.UtcNow;
            job.ErrorText = string.IsNullOrWhiteSpace(job.ErrorText)
                ? "Chat session deleted by user."
                : job.ErrorText + "\nChat session deleted by user.";
        }

        var linkedBatches = await _db.AiBatches
            .Where(x => x.ChatSessionId == sessionId)
            .ToListAsync(ct);
        foreach (var batch in linkedBatches)
            batch.ChatSessionId = null;

        _db.AiFoundryChatSessions.Remove(session);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<AiFoundryChatSendMessageResponseDto?> SendMessageAsync(
        Guid userId,
        string? userDisplayName,
        Guid sessionId,
        AiFoundryChatSendMessageRequestDto request,
        CancellationToken ct = default)
    {
        var session = await _db.AiFoundryChatSessions.FirstOrDefaultAsync(x => x.Id == sessionId && x.CreatedByUserId == userId, ct);
        if (session == null)
            return null;

        await TryFinalizePendingAsync(session, ct);
        await TryAppendGenerationUpdatesAsync(session, ct);

        var messages = DeserializeMessages(session.MessagesJson);
        var pendingAssistant = messages.LastOrDefault(IsPendingAssistant);

        if (pendingAssistant != null)
        {
            var pendingCourseMap = await LoadCourseTitleMapAsync(session.CourseId.HasValue ? new[] { session.CourseId.Value } : Array.Empty<Guid>(), ct);
            return new AiFoundryChatSendMessageResponseDto
            {
                Pending = true,
                PendingJobId = pendingAssistant.PendingJobId,
                Session = MapSession(session, pendingCourseMap, messages, BuildMemory(messages, session.PlanJson, request.InstructionStrictness)),
            };
        }

        var normalizedAttachments = NormalizeAttachments(request.Attachments);
        var userMessage = new AiFoundryChatMessageDto
        {
            Id = Guid.NewGuid(),
            Role = "user",
            Content = (request.Content ?? string.Empty).Trim(),
            CreatedAtUtc = DateTime.UtcNow,
            Status = "done",
            Attachments = normalizedAttachments,
        };
        messages.Add(userMessage);

        if (string.IsNullOrWhiteSpace(session.Title) || string.Equals(session.Title, "Новый AI-чат", StringComparison.OrdinalIgnoreCase))
            session.Title = BuildSessionTitle(messages);

        var instructionStrictness = ClampInstructionStrictness(request.InstructionStrictness, DeserializeMemory(session.PlanJson).InstructionStrictness);
        var actionMode = NormalizeActionMode(request.ActionMode);
        var refreshedMemory = BuildMemory(messages, session.PlanJson, instructionStrictness);
        refreshedMemory.LastActionMode = actionMode;
        session.PlanJson = SerializeMemory(refreshedMemory);
        var autoOverviewBootstrap = await TryAutoEnsureCourseOverviewsAsync(session.CourseId, userMessage.Content, refreshedMemory, userId, userDisplayName, ct);
        var payload = await BuildChatPayloadAsync(session, messages, actionMode, instructionStrictness, autoOverviewBootstrap, ct);
        var job = await _jobs.EnqueueAsync(new CreateAiJobRequestDto
        {
            Type = AiFoundryJobTypes.ChatTurn,
            TargetEntityType = "chat-session",
            TargetEntityId = session.Id,
            CourseId = session.CourseId,
            Priority = 30,
            InputJson = JsonSerializer.Serialize(payload, JsonOptions),
            Files = normalizedAttachments.Select(x => new AiJobFileDto
            {
                FileKey = x.FileKey,
                OriginalName = x.OriginalName,
                MimeType = x.MimeType,
                PublicUrl = x.PublicUrl,
            }).ToList(),
        }, userId, userDisplayName, ct);

        var assistantPlaceholder = new AiFoundryChatMessageDto
        {
            Id = Guid.NewGuid(),
            Role = "assistant",
            Content = normalizedAttachments.Count > 0
                ? "Приняла сообщение и вложения. Анализирую контекст и готовлю ответ…"
                : "Приняла сообщение. Анализирую контекст и готовлю ответ…",
            CreatedAtUtc = DateTime.UtcNow,
            Status = "processing",
            PendingJobId = job.Id,
        };
        messages.Add(assistantPlaceholder);

        session.MessagesJson = SerializeMessages(messages);
        session.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        var resultCourseMap = await LoadCourseTitleMapAsync(session.CourseId.HasValue ? new[] { session.CourseId.Value } : Array.Empty<Guid>(), ct);
        return new AiFoundryChatSendMessageResponseDto
        {
            Pending = true,
            PendingJobId = job.Id,
            Session = MapSession(session, resultCourseMap, messages, BuildMemory(messages, session.PlanJson, instructionStrictness)),
        };
    }

    private async Task<AiEnsureCourseAssignmentOverviewsResultDto?> TryAutoEnsureCourseOverviewsAsync(
        Guid? courseId,
        string? latestUserContent,
        AiFoundryChatMemoryDto? memory,
        Guid? userId,
        string? userDisplayName,
        CancellationToken ct)
    {
        if (!courseId.HasValue)
            return null;
        if (!ShouldAutoEnsureCourseOverviews(latestUserContent, memory))
            return null;

        try
        {
            return await _jobs.EnsureCourseAssignmentOverviewsAsync(new AiEnsureCourseAssignmentOverviewsRequestDto
            {
                CourseId = courseId.Value,
                OnlyMissing = true,
                Limit = 80,
                Priority = 4,
                AutoTriggered = true,
            }, userId, userDisplayName, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to auto-ensure course assignment overviews for course {CourseId}", courseId.Value);
            return null;
        }
    }

    private static bool ShouldAutoEnsureCourseOverviews(string? latestUserContent, AiFoundryChatMemoryDto? memory)
    {
        if (memory?.AgentState != null)
        {
            var objectiveKind = (memory.AgentState.ObjectiveKind ?? string.Empty).Trim();
            if (objectiveKind.Equals("course-gap-remediation", StringComparison.OrdinalIgnoreCase)
                || objectiveKind.Equals("course-diagnostics", StringComparison.OrdinalIgnoreCase)
                || objectiveKind.Equals("course-inspection", StringComparison.OrdinalIgnoreCase)
                || objectiveKind.Equals("bridge-planning", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        var text = (latestUserContent ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(text))
            return true;

        string[] markers =
        {
            "курс", "задани", "проанализ", "анализ", "пробел", "новые функции", "обуч",
            "мостик", "подвод", "посмотри точнее", "точно ли", "покажи задания"
        };
        return markers.Any(text.Contains);
    }

    public async Task<AiFoundryChatSendMessageResponseDto?> ConfirmToolCallAsync(
        Guid userId,
        string? userDisplayName,
        Guid sessionId,
        AiFoundryChatConfirmToolRequestDto request,
        CancellationToken ct = default)
    {
        var session = await _db.AiFoundryChatSessions.FirstOrDefaultAsync(x => x.Id == sessionId && x.CreatedByUserId == userId, ct);
        if (session == null)
            return null;

        await TryFinalizePendingAsync(session, ct);
        await TryAppendGenerationUpdatesAsync(session, ct);

        var messages = DeserializeMessages(session.MessagesJson);
        if (messages.LastOrDefault(IsPendingAssistant) != null)
        {
            var pendingCourseMap = await LoadCourseTitleMapAsync(session.CourseId.HasValue ? new[] { session.CourseId.Value } : Array.Empty<Guid>(), ct);
            return new AiFoundryChatSendMessageResponseDto
            {
                Pending = true,
                PendingJobId = messages.LastOrDefault(IsPendingAssistant)?.PendingJobId,
                Session = MapSession(session, pendingCourseMap, messages, BuildMemory(messages, session.PlanJson, request.InstructionStrictness)),
            };
        }

        var note = string.IsNullOrWhiteSpace(request.Note)
            ? BuildConfirmationRequestMessage(request.ToolName)
            : request.Note.Trim();

        var toolCall = new AiFoundryChatToolCallDto
        {
            Name = request.ToolName.Trim(),
            Reason = "Подтверждено пользователем в интерфейсе чата.",
            ArgumentsJson = SetConfirmedInArguments(request.ArgumentsJson),
        };

        messages.Add(new AiFoundryChatMessageDto
        {
            Id = Guid.NewGuid(),
            Role = "user",
            Content = note,
            CreatedAtUtc = DateTime.UtcNow,
            Status = "done",
        });

        var actionMode = NormalizeActionMode(BuildMemory(messages, session.PlanJson).LastActionMode);
        var execution = await ExecuteToolCallsAsync(session, messages, new List<AiFoundryChatToolCallDto> { toolCall }, actionMode, userId, userDisplayName, ct);
        var toolResults = execution.ToolResults;
        var primaryResult = toolResults.FirstOrDefault();
        var assistantIntro = "Подтверждение получено. Выполняю действие.";

        messages.Add(new AiFoundryChatMessageDto
        {
            Id = Guid.NewGuid(),
            Role = "assistant",
            Content = BuildAssistantContent(assistantIntro, toolResults),
            CreatedAtUtc = DateTime.UtcNow,
            Status = primaryResult?.Status == "failed" ? "failed" : "done",
            ToolCalls = execution.ToolCalls,
            ToolCall = execution.ToolCalls.FirstOrDefault(),
            ToolResults = toolResults,
            ToolResult = primaryResult,
        });

        session.PlanJson = SerializeMemory(BuildMemory(messages, session.PlanJson));
        session.MessagesJson = SerializeMessages(messages);
        session.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        var courseMap = await LoadCourseTitleMapAsync(session.CourseId.HasValue ? new[] { session.CourseId.Value } : Array.Empty<Guid>(), ct);
        return new AiFoundryChatSendMessageResponseDto
        {
            Pending = false,
            PendingJobId = null,
            Session = MapSession(session, courseMap, messages, BuildMemory(messages, session.PlanJson)),
        };
    }

    private async Task<bool> TryAppendGenerationUpdatesAsync(AiFoundryChatSession session, CancellationToken ct)
    {
        var result = await BuildGenerationUpdateToolResultAsync(session, ct, force: false, limit: 4);
        if (result == null)
            return false;

        var messages = DeserializeMessages(session.MessagesJson);
        messages.Add(new AiFoundryChatMessageDto
        {
            Id = Guid.NewGuid(),
            Role = "assistant",
            Content = result.Summary ?? "Появились новые результаты генерации.",
            CreatedAtUtc = DateTime.UtcNow,
            Status = "done",
            ToolResults = new List<AiFoundryChatToolResultDto> { result },
            ToolResult = result,
        });

        session.MessagesJson = SerializeMessages(messages);
        session.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return true;
    }

    private async Task<AiFoundryChatToolResultDto?> BuildGenerationUpdateToolResultAsync(AiFoundryChatSession session, CancellationToken ct, bool force, int limit)
    {
        var memory = DeserializeMemory(session.PlanJson);
        var announced = new HashSet<Guid>((memory.AnnouncedGenerationJobIds ?? new List<Guid>()));
        var courseId = session.CourseId;

        var jobs = await _db.AiJobs.AsNoTracking()
            .Where(x => x.CompletedAtUtc != null
                && (x.Type == "assignment_generate_from_text" || x.Type == "assignment_generate_from_file" || x.Type == "assignment_repair" || x.Type == "assignment_validate_draft")
                && (!courseId.HasValue || x.CourseId == courseId.Value))
            .OrderByDescending(x => x.CompletedAtUtc)
            .Take(120)
            .Select(x => new { x.Id, x.Type, x.Status, x.TargetEntityType, x.TargetEntityId, x.CompletedAtUtc, x.InputJson })
            .ToListAsync(ct);

        var sessionLinkedJobIds = jobs
            .Where(x => !announced.Contains(x.Id) && IsChatSessionLinkedJob(x.InputJson, session.Id))
            .Select(x => x.Id)
            .ToHashSet();

        if (sessionLinkedJobIds.Count == 0 && !force)
            return null;

        var drafts = await _db.AiGeneratedAssignmentDrafts.AsNoTracking()
            .Where(x => x.CourseId == courseId && (sessionLinkedJobIds.Contains(x.JobId) || (x.ParentJobId.HasValue && sessionLinkedJobIds.Contains(x.ParentJobId.Value))))
            .OrderByDescending(x => x.UpdatedAtUtc)
            .Take(Math.Max(limit, 8))
            .Select(x => new { x.Id, x.JobId, x.ParentJobId, x.Title, x.Status, x.AssignmentType, x.DraftJson, x.UpdatedAtUtc })
            .ToListAsync(ct);

        if (drafts.Count == 0 && !force)
            return null;

        var targetDrafts = drafts.Take(limit).ToList();
        foreach (var d in targetDrafts)
        {
            announced.Add(d.JobId);
            if (d.ParentJobId.HasValue) announced.Add(d.ParentJobId.Value);
        }
        memory.AnnouncedGenerationJobIds = announced.TakeLast(200).ToList();
        session.PlanJson = SerializeMemory(memory);

        var sb = new StringBuilder();
        if (targetDrafts.Count == 0)
        {
            sb.Append("Пока не появилось новых завершённых черновиков по этой чат-сессии.");
        }
        else
        {
            sb.AppendLine(targetDrafts.Count == 1
                ? "Готов новый черновик. Я уже подтянула его в контекст чата — можешь сразу написать, что в нём поправить."
                : $"Готовы новые черновики ({targetDrafts.Count}). Я уже подтянула их в контекст чата — можешь сразу написать, что и где поправить.");
            sb.AppendLine();
            var idx = 1;
            foreach (var draft in targetDrafts)
            {
                var snippet = BuildDraftChatSnippet(draft.DraftJson);
                sb.Append(idx++).Append(") ").Append(draft.Title).Append(" — статус: ").Append(draft.Status);
                if (!string.IsNullOrWhiteSpace(snippet))
                    sb.Append(". ").Append(snippet);
                sb.AppendLine();
            }
            sb.AppendLine();
            sb.Append("Напиши обычным сообщением, например: «во второй задаче убери эту фразу», «в первой поменяй тесты», «третью сделай ближе к стилю первой задачи». ");
        }

        return new AiFoundryChatToolResultDto
        {
            Status = "done",
            Summary = sb.ToString().Trim(),
            CourseId = courseId,
            DraftId = targetDrafts.Count == 1 ? targetDrafts[0].Id : null,
            JobId = targetDrafts.Count == 1 ? targetDrafts[0].JobId : null,
            NavigateTo = "/admin/ai/chat?sessionId=" + session.Id,
        };
    }

    private static bool IsChatSessionLinkedJob(string? inputJson, Guid sessionId)
    {
        if (string.IsNullOrWhiteSpace(inputJson))
            return false;
        return inputJson.Contains(sessionId.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildDraftChatSnippet(string? draftJson)
    {
        if (string.IsNullOrWhiteSpace(draftJson))
            return string.Empty;
        try
        {
            using var doc = JsonDocument.Parse(draftJson);
            var root = doc.RootElement;
            var description = ReadString(root, "description");
            if (string.IsNullOrWhiteSpace(description) && root.TryGetProperty("draft", out var nested) && nested.ValueKind == JsonValueKind.Object)
                description = ReadString(nested, "description");
            description = ShortenSingleLine(description ?? string.Empty, 220);
            return description ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private async Task WaitForJobCompletionAsync(Guid jobId, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.Add(timeout);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var jobState = await _db.AiJobs.AsNoTracking()
                .Where(x => x.Id == jobId)
                .Select(x => new { x.Status })
                .FirstOrDefaultAsync(ct);

            var status = (jobState?.Status ?? string.Empty).Trim().ToLowerInvariant();
            if (status is "done" or "failed" or "error" or "cancelled")
                return;

            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
    }

    private async Task<bool> TryFinalizePendingAsync(AiFoundryChatSession session, CancellationToken ct)
    {
        var messages = DeserializeMessages(session.MessagesJson);
        var assistantMessage = messages.LastOrDefault(IsPendingAssistant);
        if (assistantMessage?.PendingJobId is not Guid jobId)
            return false;

        var job = await _db.AiJobs.AsNoTracking().FirstOrDefaultAsync(x => x.Id == jobId, ct);
        if (job == null)
        {
            assistantMessage.Status = "failed";
            assistantMessage.Content = "AI job не найден. Попробуйте отправить сообщение ещё раз.";
            assistantMessage.PendingJobId = null;
            session.PlanJson = SerializeMemory(BuildMemory(messages, session.PlanJson));
            session.MessagesJson = SerializeMessages(messages);
            session.UpdatedAtUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            return true;
        }

        var status = (job.Status ?? string.Empty).Trim().ToLowerInvariant();
        if (status is "pending" or "running" or "processing")
            return false;

        if (status is "failed" or "error" or "cancelled")
        {
            assistantMessage.Status = "failed";
            assistantMessage.Content = string.IsNullOrWhiteSpace(job.ErrorText)
                ? "Не удалось получить ответ от AI."
                : $"Не удалось получить ответ от AI: {job.ErrorText}";
            assistantMessage.PendingJobId = null;
            session.PlanJson = SerializeMemory(BuildMemory(messages, session.PlanJson));
            session.MessagesJson = SerializeMessages(messages);
            session.UpdatedAtUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            return true;
        }

        var root = ParseJson(job.ResultJson);
        var (assistantText, toolCalls) = InterpretChatTurn(root, session, messages);
        var actionMode = NormalizeActionMode(BuildMemory(messages, session.PlanJson).LastActionMode);
        var execution = await ExecuteToolCallsAsync(session, messages, toolCalls, actionMode, job.CreatedByUserId ?? session.CreatedByUserId, job.CreatedByDisplayName, ct);
        var accumulatedToolCalls = assistantMessage.ToolCalls.Concat(execution.ToolCalls).ToList();
        var accumulatedToolResults = assistantMessage.ToolResults.Concat(execution.ToolResults).ToList();
        var memoryAfterTools = BuildMemory(messages, session.PlanJson);

        if (ShouldContinueInternalReasoningPass(actionMode, session, messages, memoryAfterTools, accumulatedToolCalls, accumulatedToolResults, toolCalls))
        {
            var followupPayload = await BuildChatPayloadAsync(session, messages, actionMode, memoryAfterTools.InstructionStrictness, null, ct);
            var followupJob = await _jobs.EnqueueAsync(new CreateAiJobRequestDto
            {
                Type = AiFoundryJobTypes.ChatTurn,
                TargetEntityType = "chat-session",
                TargetEntityId = session.Id,
                CourseId = session.CourseId,
                Priority = 30,
                InputJson = JsonSerializer.Serialize(followupPayload, JsonOptions),
            }, job.CreatedByUserId ?? session.CreatedByUserId, job.CreatedByDisplayName, ct);

            assistantMessage.Status = "processing";
            assistantMessage.PendingJobId = followupJob.Id;
            assistantMessage.ToolCalls = accumulatedToolCalls;
            assistantMessage.ToolCall = assistantMessage.ToolCalls.FirstOrDefault();
            assistantMessage.ToolResults = accumulatedToolResults;
            assistantMessage.ToolResult = assistantMessage.ToolResults.LastOrDefault() ?? assistantMessage.ToolResults.FirstOrDefault();
            assistantMessage.Content = BuildContinuationProcessingMessage(accumulatedToolCalls, accumulatedToolResults, memoryAfterTools);

            session.PlanJson = SerializeMemory(memoryAfterTools);
            session.MessagesJson = SerializeMessages(messages);
            session.UpdatedAtUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            return true;
        }

        var loopGuardTriggered = HasRepeatedBlueprintRevisionLoop(accumulatedToolCalls, accumulatedToolResults);
        var assistantIntro = loopGuardTriggered
            ? BuildLoopGuardAssistantMessage(accumulatedToolResults, memoryAfterTools)
            : assistantText;

        assistantMessage.Status = "done";
        assistantMessage.PendingJobId = null;
        assistantMessage.Experimental = ParseExperimentalTrace(root);
        assistantMessage.ToolCalls = accumulatedToolCalls;
        assistantMessage.ToolCall = assistantMessage.ToolCalls.FirstOrDefault();
        assistantMessage.ToolResults = accumulatedToolResults;
        assistantMessage.ToolResult = assistantMessage.ToolResults.LastOrDefault() ?? assistantMessage.ToolResults.FirstOrDefault();
        assistantMessage.Content = BuildAssistantContent(assistantIntro, assistantMessage.ToolResults);

        var titleSuggestion = root?["sessionTitle"]?.ToString()?.Trim();
        if (!string.IsNullOrWhiteSpace(titleSuggestion))
            session.Title = ClampTitle(titleSuggestion!);
        else if (string.IsNullOrWhiteSpace(session.Title))
            session.Title = BuildSessionTitle(messages);

        session.PlanJson = SerializeMemory(BuildMemory(messages, session.PlanJson));
        session.MessagesJson = SerializeMessages(messages);
        session.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return true;
    }

    private async Task<ChatToolExecutionDto> ExecuteToolCallsAsync(
        AiFoundryChatSession session,
        List<AiFoundryChatMessageDto> messages,
        IReadOnlyList<AiFoundryChatToolCallDto> toolCalls,
        string actionMode,
        Guid? createdByUserId,
        string? createdByDisplayName,
        CancellationToken ct)
    {
        var execution = new ChatToolExecutionDto();
        JsonObject? lastArgs = null;
        AiFoundryChatToolResultDto? lastResult = null;
        var requestedNames = new List<string>();

        foreach (var toolCall in toolCalls.Take(3))
        {
            if (toolCall == null || string.IsNullOrWhiteSpace(toolCall.Name))
                continue;

            execution.ToolCalls.Add(toolCall);
            requestedNames.Add(toolCall.Name.Trim());
            lastArgs = ParseArgumentsObject(toolCall.ArgumentsJson);
            lastResult = await ExecuteToolCallAsync(session, messages, toolCall, createdByUserId, createdByDisplayName, ct);
            if (lastResult != null)
            {
                lastResult.ActionName ??= toolCall.Name.Trim();
                execution.ToolResults.Add(lastResult);
            }

            if (ShouldStopAutoAgentLoop(toolCall.Name, lastResult))
                return execution;
        }

        if (!ShouldAutoContinueAgent(actionMode, toolCalls, execution.ToolResults))
            return execution;

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var executedCall in execution.ToolCalls)
            visited.Add(BuildToolCallLoopSignature(executedCall));

        var autoNames = new List<string>();
        for (var step = 0; step < 5; step++)
        {
            var courseId = ReadGuid(lastArgs, "courseId") ?? session.CourseId;
            if (!courseId.HasValue)
                break;

            var nextArgs = lastArgs ?? new JsonObject();
            var nextToolCall = BuildNextAgentToolCall(courseId.Value, session, messages, nextArgs);
            if (nextToolCall == null || string.IsNullOrWhiteSpace(nextToolCall.Name))
                break;

            var signature = BuildToolCallLoopSignature(nextToolCall);
            if (!visited.Add(signature))
                break;

            execution.ToolCalls.Add(nextToolCall);
            autoNames.Add(nextToolCall.Name.Trim());
            lastArgs = ParseArgumentsObject(nextToolCall.ArgumentsJson);
            lastResult = await ExecuteToolCallAsync(session, messages, nextToolCall, createdByUserId, createdByDisplayName, ct);
            if (lastResult != null)
            {
                lastResult.ActionName ??= nextToolCall.Name.Trim();
                execution.ToolResults.Add(lastResult);
            }

            if (ShouldStopAutoAgentLoop(nextToolCall.Name, lastResult) || AutonomousTerminalActionNames.Contains(nextToolCall.Name.Trim()))
                break;
        }

        if (autoNames.Count == 0)
            return execution;

        var finalResult = execution.ToolResults.LastOrDefault();
        var traceSummary = BuildAgentLoopSummary(requestedNames, autoNames, finalResult);
        execution.AgentTrace = traceSummary;
        if (finalResult != null)
        {
            var requestedNodes = new JsonArray();
            foreach (var item in requestedNames)
                requestedNodes.Add(item);
            var autoNodes = new JsonArray();
            foreach (var item in autoNames)
                autoNodes.Add(item);
            AttachToolResultDebugInfo(finalResult, "agentLoop", new JsonObject
            {
                ["requestedActions"] = requestedNodes,
                ["autoActions"] = autoNodes,
                ["traceSummary"] = traceSummary,
                ["toolCallsExecuted"] = execution.ToolCalls.Count,
                ["toolResultsSeen"] = execution.ToolResults.Count,
            });
            execution.ToolResults.Clear();
            execution.ToolResults.Add(finalResult);
        }
        return execution;
    }

    private async Task<AiFoundryChatToolResultDto?> ExecuteToolCallAsync(
        AiFoundryChatSession session,
        List<AiFoundryChatMessageDto> messages,
        AiFoundryChatToolCallDto? toolCall,
        Guid? createdByUserId,
        string? createdByDisplayName,
        CancellationToken ct)
    {
        if (toolCall == null || string.IsNullOrWhiteSpace(toolCall.Name))
            return null;

        var action = toolCall.Name.Trim().ToLowerInvariant();
        if (action is "none" or "reply_only" or "reply-only")
            return null;

        JsonObject args = new();
        try
        {
            args = JsonNode.Parse(toolCall.ArgumentsJson ?? "{}") as JsonObject ?? new JsonObject();
        }
        catch
        {
            args = new JsonObject();
        }

        var instructionStrictness = ClampInstructionStrictness(
            ReadInt(args, "instructionStrictness"),
            DeserializeMemory(session.PlanJson).InstructionStrictness);

        try
        {
            switch (action)
            {
                case "queue_generate_batch":
                {
                    var courseId = ReadGuid(args, "courseId") ?? session.CourseId;
                    if (!courseId.HasValue)
                        return FailTool("Нужно выбрать courseId, прежде чем запускать генерацию.");

                    var prompt = ReadString(args, "prompt") ?? BuildFallbackPrompt(messages);
                    var requestedCount = Math.Clamp(ReadInt(args, "count") ?? 5, 1, 12);
                    var difficulty = Math.Clamp(ReadInt(args, "difficulty") ?? 2, 1, 5);
                    var memory = BuildMemory(messages, session.PlanJson, instructionStrictness);
                    var assignmentType = ReadString(args, "assignmentType") ?? "code-test";
                    var sourceText = ReadString(args, "sourceText") ?? RewriteSourceTextForStepByStepSeries(prompt, memory, requestedCount);
                    prompt = RewritePromptForStepByStepSeries(prompt, memory, requestedCount);
                    var titleHint = AiGenerationScenarioPromptAdapter.SuggestTitleHint(memory, prompt, sourceText, requestedCount, ReadString(args, "titleHint"));
                    var notes = ReadString(args, "notes");
                    var structuredContextJson = BuildStructuredBatchContextJson(session, messages, memory, courseId.Value, prompt, args, DetermineDirectGenerationMode(memory, prompt, sourceText, requestedCount), requestedCount, difficulty);

                    return await EnqueueDirectTextGenerationAsync(
                        session,
                        messages,
                        courseId.Value,
                        assignmentType,
                        prompt,
                        sourceText,
                        titleHint,
                        difficulty,
                        requestedCount,
                        notes,
                        structuredContextJson,
                        Math.Clamp(ReadInt(args, "priority") ?? 20, 1, 100),
                        ReadBool(args, "enableSelfCheck") ?? true,
                        instructionStrictness,
                        memory.LatestExplicitInstruction,
                        memory.LatestTeachingScript,
                        createdByUserId,
                        createdByDisplayName,
                        ct,
                        selectedProposal: null,
                        blueprint: null,
                        successSummarySingle: "Поставил в очередь генерацию задания из текста.",
                        successSummaryMultiTemplate: "Поставил в очередь прямую генерацию {0} задач отдельными job без batch."
                    );
                }
                case "analyze_course_progression":
                {
                    var courseId = ReadGuid(args, "courseId") ?? session.CourseId;
                    if (!courseId.HasValue)
                        return FailTool("Нужно выбрать courseId, прежде чем анализировать курс.");

                    var report = await AnalyzeCourseProgressionAsync(courseId.Value, ReadString(args, "focus"), ReadInt(args, "limitAssignments"), ct);
                    if (report == null)
                        return FailTool("Курс для анализа не найден или в нём пока нет заданий.");

                    session.PlanJson = SerializeMemory(WithLastCourseAudit(BuildMemory(messages, session.PlanJson), report));

                    return new AiFoundryChatToolResultDto
                    {
                        Status = report.Findings.Count == 0 ? "done" : "done",
                        Summary = BuildCourseAuditSummary(report),
                        CourseId = courseId,
                        NavigateTo = "/admin/ai",
                    };
                }
                case "inspect_course_assignments":
                {
                    var courseId = ReadGuid(args, "courseId") ?? session.CourseId;
                    if (!courseId.HasValue)
                        return FailTool("Нужно выбрать courseId, прежде чем открывать задания курса.");

                    var inspection = await InspectCourseAssignmentsAsync(
                        courseId.Value,
                        ReadString(args, "query"),
                        ReadGuid(args, "aroundAssignmentId"),
                        ReadInt(args, "window"),
                        ReadInt(args, "limitAssignments"),
                        ReadBool(args, "includeFirstTaskStyleAnchor") ?? false,
                        ct);
                    if (inspection == null || inspection.Assignments.Count == 0)
                        return FailTool("Не нашла подходящие задания курса для просмотра. Попробуй сузить query или выбрать другой courseId.");

                    session.PlanJson = SerializeMemory(WithLastCourseInspection(BuildMemory(messages, session.PlanJson), inspection));

                    return new AiFoundryChatToolResultDto
                    {
                        Status = "done",
                        Summary = BuildCourseInspectionSummary(inspection),
                        CourseId = courseId,
                        NavigateTo = "/admin/ai",
                    };
                }
                case "prepare_bridge_plan":
                {
                    var courseId = ReadGuid(args, "courseId") ?? session.CourseId;
                    if (!courseId.HasValue)
                        return FailTool("Нужно выбрать courseId, прежде чем собирать план мостиков.");

                    var memory = DeserializeMemory(session.PlanJson);
                    var audit = memory.LastCourseAudit != null && memory.LastCourseAudit.CourseId == courseId.Value ? memory.LastCourseAudit : null;
                    var inspection = memory.LastCourseInspection != null && memory.LastCourseInspection.CourseId == courseId.Value ? memory.LastCourseInspection : null;
                    var focus = ReadString(args, "focus");
                    var requestedCount = Math.Clamp(ReadInt(args, "count") ?? 6, 1, 12);

                    // ── Strict checks: no data = stop and ask user ──
                    if (string.IsNullOrWhiteSpace(focus) && (audit == null || string.IsNullOrWhiteSpace(audit.Focus)))
                        return FailTool("Нет фокуса. Спроси пользователя: какую тему нужно закрыть мостиками? Например: 'объяснить cout и первый вывод', 'переменные и типы', 'условия if/else'. Без явной темы генерация заблокирована.");

                    if (audit == null)
                        return FailTool("Нет аудита курса. Сначала вызови analyze_course_progression с фокусом пользователя, чтобы понять где именно пробелы в курсе.");

                    // Build plan from audit findings (if any)
                    AiFoundryBridgePlanDto? bridgePlan = null;
                    if (audit.Findings.Count > 0)
                    {
                        var selectedFindings = SelectAuditFindings(audit, args);
                        var inspectionAnchor = BuildInspectionPlacementCandidate(inspection);
                        var requestedAfterAssignmentId = ResolveRequestedAfterAssignmentId(args, memory) ?? inspectionAnchor?.AfterAssignmentId;
                        if (selectedFindings.Count > 0)
                            bridgePlan = BuildBridgePlan(audit, inspection, selectedFindings, focus, requestedAfterAssignmentId, requestedCount);
                    }

                    // If audit had no findings or BuildBridgePlan returned empty, try BuildDetailedBridgePlanFromAnchor
                    // but ONLY if we have a focus (already checked above)
                    if (bridgePlan == null || bridgePlan.Items.Count == 0)
                    {
                        // Determine anchor: from audit finding, or from inspection, or from args
                        Guid? anchorId = null;
                        string? anchorTitle = null;
                        if (audit.Findings.Count > 0)
                        {
                            var bestFinding = audit.Findings.First();
                            anchorId = bestFinding.AfterAssignmentId ?? bestFinding.BeforeAssignmentId;
                            anchorTitle = bestFinding.AfterAssignmentTitle ?? bestFinding.BeforeAssignmentTitle;
                        }
                        anchorId ??= ResolveRequestedAfterAssignmentId(args, memory) ?? BuildInspectionPlacementCandidate(inspection)?.AfterAssignmentId;
                        if (anchorId.HasValue)
                        {
                            anchorTitle ??= ResolveRequestedAfterAssignmentTitle(memory, anchorId)
                                ?? await _db.TaskAssignments.AsNoTracking()
                                    .Where(x => x.Id == anchorId.Value)
                                    .Select(x => x.Title)
                                    .FirstOrDefaultAsync(ct);
                            bridgePlan = BuildDetailedBridgePlanFromAnchor(
                                audit, inspection, anchorId.Value, anchorTitle,
                                focus, requestedCount, memory.AgentState);
                        }
                    }

                    if (bridgePlan == null || bridgePlan.Items.Count == 0)
                    {
                        var reason = audit.Findings.Count == 0
                            ? $"Аудит курса не нашёл педагогических пробелов по фокусу «{focus ?? audit.Focus}». Спроси пользователя: где именно он видит проблему? После какого задания нужны мостики?"
                            : $"Не удалось построить план. Аудит нашёл {audit.Findings.Count} пробел(ов), но ни один не подошёл для плана. Спроси пользователя: какой именно пробел он имеет в виду? После какого задания вставить мостики?";
                        return FailTool(reason);
                    }

                    session.PlanJson = SerializeMemory(WithLastBridgePlan(BuildMemory(messages, session.PlanJson), bridgePlan));

                    return new AiFoundryChatToolResultDto
                    {
                        Status = "done",
                        Summary = BuildBridgePlanSummary(bridgePlan),
                        CourseId = courseId,
                        NavigateTo = "/admin/ai",
                    };
                }
                case "show_bridge_plan":
                {
                    var courseId = ReadGuid(args, "courseId") ?? session.CourseId;
                    if (!courseId.HasValue)
                        return FailTool("Нужно выбрать courseId, прежде чем показывать план мостиков.");

                    var memory = DeserializeMemory(session.PlanJson);
                    var bridgePlan = memory.LastBridgePlan;
                    if (bridgePlan == null || bridgePlan.CourseId != courseId.Value || bridgePlan.Items.Count == 0)
                        return FailTool("Плана мостиков для этого курса пока нет. Сначала запусти prepare_bridge_plan.");

                    return new AiFoundryChatToolResultDto
                    {
                        Status = "done",
                        Summary = BuildBridgePlanSummary(bridgePlan),
                        CourseId = courseId,
                        NavigateTo = "/admin/ai",
                    };
                }
                case "revise_bridge_plan":
                {
                    var courseId = ReadGuid(args, "courseId") ?? session.CourseId;
                    if (!courseId.HasValue)
                        return FailTool("Нужно выбрать courseId, прежде чем править план мостиков.");

                    var memory = DeserializeMemory(session.PlanJson);
                    var bridgePlan = memory.LastBridgePlan;
                    if (bridgePlan == null || bridgePlan.CourseId != courseId.Value || bridgePlan.Items.Count == 0)
                        return FailTool("Плана мостиков для этого курса пока нет. Сначала запусти prepare_bridge_plan.");

                    var revisedPlan = await ReviseBridgePlanAsync(bridgePlan, memory.LastCourseInspection, args, ct);
                    session.PlanJson = SerializeMemory(WithLastBridgePlan(BuildMemory(messages, session.PlanJson), revisedPlan));

                    return new AiFoundryChatToolResultDto
                    {
                        Status = "done",
                        Summary = BuildBridgePlanSummary(revisedPlan),
                        CourseId = courseId,
                        NavigateTo = "/admin/ai",
                    };
                }
                case "advance_agent_stage":
                {
                    var courseId = ReadGuid(args, "courseId") ?? session.CourseId;
                    if (!courseId.HasValue)
                        return FailTool("Нужно выбрать courseId, прежде чем продолжать агента.");

                    var nextToolCall = BuildNextAgentToolCall(courseId.Value, session, messages, args);
                    if (nextToolCall == null)
                        return FailTool("Не нашла следующий шаг для агента. Попробуй явно попросить аудит курса, показать план или сгенерировать мостики.");

                    var nestedResult = await ExecuteToolCallAsync(session, messages, nextToolCall, createdByUserId, createdByDisplayName, ct);
                    if (nestedResult == null)
                        return FailTool("Не удалось выполнить следующий шаг агента.");

                    nestedResult.Summary = $"Автопродолжение агента: {nestedResult.Summary}";
                    return nestedResult;
                }
                case "queue_generate_bridge_batch":
                {
                    var courseId = ReadGuid(args, "courseId") ?? session.CourseId;
                    if (!courseId.HasValue)
                        return FailTool("Нужно выбрать courseId, прежде чем запускать генерацию мостиков.");

                    var memory = DeserializeMemory(session.PlanJson);
                    var audit = EnsureBridgeAudit(courseId.Value, memory.LastCourseAudit, memory.LastCourseInspection, memory.LastBridgePlan);
                    var bridgePlan = memory.LastBridgePlan != null && memory.LastBridgePlan.CourseId == courseId.Value
                        ? memory.LastBridgePlan
                        : null;

                    if (bridgePlan == null)
                        return FailTool("Нет готового плана мостиков. Сначала вызови prepare_bridge_plan с явным focus от пользователя. Без плана генерация заблокирована.");

                    if (bridgePlan.Items.Count == 0)
                        return FailTool("План мостиков пустой (0 items). Это значит что prepare_bridge_plan не смог построить план. Спроси пользователя какую тему он хочет закрыть и повторно вызови prepare_bridge_plan с focus.");

                    var selectedItems = SelectBridgePlanItems(bridgePlan, args);
                    if (selectedItems.Count == 0)
                        return FailTool("Не удалось выбрать plan items для генерации. Проверь itemIndexes или сначала обнови план мостиков.");

                    session.PlanJson = SerializeMemory(WithLastBridgePlan(BuildMemory(messages, session.PlanJson), bridgePlan));

                    var requestedCount = Math.Clamp(ReadInt(args, "count") ?? selectedItems.Sum(x => Math.Max(1, x.TaskCount)), 1, 12);
                    var difficulty = Math.Clamp(ReadInt(args, "difficulty") ?? Math.Max(1, Math.Min(3, selectedItems.Max(x => x.Difficulty))), 1, 3);
                    var prompt = BuildBridgeBatchPrompt(audit, bridgePlan, selectedItems, memory, ReadString(args, "prompt"), ReadString(args, "focus"));
                    var notes = BuildBridgeBatchNotes(audit, bridgePlan, selectedItems);

                    var memoryForBatch = BuildMemory(messages, session.PlanJson);
                    var structuredContextJson = BuildStructuredBatchContextJson(session, messages, memoryForBatch, courseId.Value, prompt, args, "bridge-pack", requestedCount, difficulty);

                    return await EnqueueDirectTextGenerationAsync(
                        session,
                        messages,
                        courseId.Value,
                        "code-test",
                        prompt,
                        prompt,
                        "Мостик",
                        difficulty,
                        requestedCount,
                        notes,
                        structuredContextJson,
                        Math.Clamp(ReadInt(args, "priority") ?? 20, 1, 100),
                        true,
                        instructionStrictness,
                        memory.LatestExplicitInstruction,
                        memory.LatestTeachingScript,
                        createdByUserId,
                        createdByDisplayName,
                        ct,
                        selectedProposal: null,
                        blueprint: null,
                        successSummarySingle: "Поставил в очередь генерацию мостика по текущему плану.",
                        successSummaryMultiTemplate: "Поставил в очередь прямую генерацию {0} мостиков отдельными job без batch."
                    );
                }
                case "save_chat_blueprint":
                {
                    var courseId = ReadGuid(args, "courseId") ?? session.CourseId;
                    if (!courseId.HasValue)
                        return FailTool("Нужно выбрать courseId, прежде чем сохранять примерные условия.");

                    var proposals = ReadChatBlueprintProposals(args);
                    if (proposals.Count == 0)
                        return FailTool("Не удалось сохранить примерные условия: proposals пустой или сломан.");

                    var memory = BuildMemory(messages, session.PlanJson);
                    var defaultAfterAssignmentId = ResolveRequestedAfterAssignmentId(args, memory);
                    var defaultAfterAssignmentTitle = ResolveRequestedAfterAssignmentTitle(memory, defaultAfterAssignmentId);
                    foreach (var proposal in proposals)
                    {
                        if (!proposal.PlacementAfterAssignmentId.HasValue && defaultAfterAssignmentId.HasValue)
                            proposal.PlacementAfterAssignmentId = defaultAfterAssignmentId;
                        if (string.IsNullOrWhiteSpace(proposal.PlacementAfterTitle) && !string.IsNullOrWhiteSpace(defaultAfterAssignmentTitle))
                            proposal.PlacementAfterTitle = defaultAfterAssignmentTitle;
                        if (string.IsNullOrWhiteSpace(proposal.PlacementReason) && (!string.IsNullOrWhiteSpace(proposal.PlacementAfterTitle) || proposal.PlacementAfterAssignmentId.HasValue))
                            proposal.PlacementReason = $"Поставить после «{proposal.PlacementAfterTitle ?? "выбранного задания"}», чтобы новая задача логично продолжала текущую лестницу курса.";
                    }
                    var blueprintValidation = ValidateChatBlueprintProposals(memory, proposals, args);
                    if (blueprintValidation != null)
                    {
                        _log.LogWarning("[AiChatBlueprintValidation] session={SessionId} action=save_chat_blueprint status={Status} debug={Debug}",
                            session.Id,
                            blueprintValidation.Status,
                            blueprintValidation.DebugInfo?.ToJsonString(JsonOptions) ?? "{}");
                        return blueprintValidation;
                    }

                    var nextRevision = Math.Max(ReadInt(args, "revision") ?? ((memory.CurrentDraftBlueprint?.Revision ?? 0) + 1), 1);
                    var blueprint = new AiFoundryChatDraftBlueprintDto
                    {
                        Summary = ReadString(args, "summary") ?? BuildChatBlueprintSummaryText(proposals),
                        UpdatedAtUtc = DateTime.UtcNow,
                        Revision = nextRevision,
                        Source = "chat",
                        ApprovedForDraft = ReadBool(args, "approvedForDraft") ?? false,
                        Proposals = proposals,
                    };
                    session.PlanJson = SerializeMemory(WithCurrentDraftBlueprint(memory, blueprint));

                    return new AiFoundryChatToolResultDto
                    {
                        Status = "done",
                        Summary = BuildChatBlueprintSummary(blueprint, memory),
                        CourseId = courseId,
                        NavigateTo = "/admin/ai/chat?sessionId=" + session.Id,
                        DebugInfo = BuildBlueprintRoutingDebugInfo("save_chat_blueprint", memory, args, proposals),
                    };
                }
                case "revise_chat_blueprint":
                {
                    var courseId = ReadGuid(args, "courseId") ?? session.CourseId;
                    if (!courseId.HasValue)
                        return FailTool("Нужно выбрать courseId, прежде чем править примерные условия.");

                    var memory = BuildMemory(messages, session.PlanJson);
                    var current = memory.CurrentDraftBlueprint;

                    // revise_chat_blueprint can also be used as a silent repair for a failed first
                    // save_chat_blueprint attempt. In that path the validator rejected the raw
                    // blueprint, so CurrentDraftBlueprint is still null, but the repaired proposals
                    // are already present in the tool arguments. Treat it as a bootstrap revise
                    // instead of failing and leaking pipeline internals back to the user.
                    var proposals = ReadChatBlueprintProposals(args, current);
                    if (proposals.Count == 0)
                    {
                        if (current == null || current.Proposals.Count == 0)
                            return FailTool("В этой сессии пока нет сохранённых примерных условий. Сначала собери их через чат.");
                        return FailTool("Не удалось обновить примерные условия: proposals пустой или сломан.");
                    }

                    var blueprintValidation = ValidateChatBlueprintProposals(memory, proposals, args);
                    if (blueprintValidation != null)
                    {
                        _log.LogWarning("[AiChatBlueprintValidation] session={SessionId} action=revise_chat_blueprint status={Status} debug={Debug}",
                            session.Id,
                            blueprintValidation.Status,
                            blueprintValidation.DebugInfo?.ToJsonString(JsonOptions) ?? "{}");
                        return blueprintValidation;
                    }

                    var nextRevision = Math.Max(ReadInt(args, "revision") ?? ((current?.Revision ?? 0) + 1), 1);
                    var blueprint = new AiFoundryChatDraftBlueprintDto
                    {
                        Summary = ReadString(args, "summary") ?? BuildChatBlueprintSummaryText(proposals),
                        UpdatedAtUtc = DateTime.UtcNow,
                        Revision = nextRevision,
                        Source = current?.Source ?? "chat",
                        ApprovedForDraft = ReadBool(args, "approvedForDraft") ?? false,
                        Proposals = proposals,
                    };
                    session.PlanJson = SerializeMemory(WithCurrentDraftBlueprint(memory, blueprint));

                    return new AiFoundryChatToolResultDto
                    {
                        Status = "done",
                        Summary = BuildChatBlueprintSummary(blueprint, memory),
                        CourseId = courseId,
                        NavigateTo = "/admin/ai/chat?sessionId=" + session.Id,
                        DebugInfo = BuildBlueprintRoutingDebugInfo("revise_chat_blueprint", memory, args, proposals),
                    };
                }
                case "show_chat_blueprint":
                {
                    var courseId = ReadGuid(args, "courseId") ?? session.CourseId;
                    if (!courseId.HasValue)
                        return FailTool("Нужно выбрать courseId, прежде чем показывать примерные условия.");

                    var memory = DeserializeMemory(session.PlanJson);
                    var blueprint = memory.CurrentDraftBlueprint;
                    if (blueprint == null || blueprint.Proposals.Count == 0)
                        return FailTool("В этой сессии пока нет сохранённых примерных условий. Сначала обсуди задачу со мной, и я соберу черновой вариант.");

                    return new AiFoundryChatToolResultDto
                    {
                        Status = "done",
                        Summary = BuildChatBlueprintSummary(blueprint, memory),
                        CourseId = courseId,
                        NavigateTo = "/admin/ai/chat?sessionId=" + session.Id,
                    };
                }
                case "drop_chat_blueprint":
                {
                    var memory = BuildMemory(messages, session.PlanJson);
                    session.PlanJson = SerializeMemory(WithCurrentDraftBlueprint(memory, null));
                    return new AiFoundryChatToolResultDto
                    {
                        Status = "done",
                        Summary = "Сбросила сохранённые примерные условия из памяти этой сессии. Можно собирать новые варианты с нуля.",
                        CourseId = ReadGuid(args, "courseId") ?? session.CourseId,
                        NavigateTo = "/admin/ai/chat?sessionId=" + session.Id,
                    };
                }
                case "finalize_chat_blueprint":
                {
                    var courseId = ReadGuid(args, "courseId") ?? session.CourseId;
                    if (!courseId.HasValue)
                        return FailTool("Нужно выбрать courseId, прежде чем превращать условия из чата в черновики.");

                    var memory = BuildMemory(messages, session.PlanJson);
                    var blueprint = memory.CurrentDraftBlueprint;
                    if (blueprint == null || blueprint.Proposals.Count == 0)
                        return FailTool("В памяти этой сессии нет согласованных примерных условий. Сначала собери и обсуди варианты, потом уже превращай их в черновики.");

                    var selectedProposalIds = ReadGuidList(args, "proposalIds");
                    var selected = selectedProposalIds.Count == 0
                        ? blueprint.Proposals.ToList()
                        : blueprint.Proposals.Where(x => selectedProposalIds.Contains(x.Id)).ToList();
                    if (selected.Count == 0)
                        return FailTool("Не нашла ни одного подходящего варианта для финализации. Проверь proposalIds или сначала покажи текущие варианты.");

                    var strictness = ClampInstructionStrictness(ReadInt(args, "instructionStrictness"), memory.InstructionStrictness);
                    var enableSelfCheck = ReadBool(args, "enableSelfCheck") ?? true;
                    var priority = Math.Clamp(ReadInt(args, "priority") ?? 20, 1, 100);
                    var createdJobs = new List<AiJobDetailsDto>();
                    foreach (var proposal in selected)
                    {
                        var job = await _jobs.QueueGenerateAssignmentFromTextAsync(new AiGenerateAssignmentFromTextRequestDto
                        {
                            CourseId = courseId.Value,
                            AssignmentType = string.IsNullOrWhiteSpace(proposal.AssignmentType) ? "code-test" : proposal.AssignmentType,
                            Prompt = BuildPromptFromChatBlueprintProposal(proposal, memory),
                            SourceText = BuildSourceTextFromChatBlueprintProposal(proposal, memory),
                            TitleHint = proposal.Title,
                            Difficulty = Math.Clamp(proposal.Difficulty, 1, 5),
                            Count = 1,
                            Notes = $"Finalized from chat blueprint session {session.Id}. Revision {blueprint.Revision}.",
                            StructuredContextJson = BuildStructuredContextFromChatBlueprintProposal(proposal, blueprint, session.Id),
                            Priority = priority,
                            EnableSelfCheck = enableSelfCheck,
                            InstructionStrictness = strictness,
                            UserInstructionSnapshot = memory.LatestExplicitInstruction,
                            TeachingScript = memory.LatestTeachingScript,
                            ChatSessionId = session.Id,
                        }, createdByUserId, createdByDisplayName, ct);
                        createdJobs.Add(job);
                        proposal.Status = "queued";
                    }
                    blueprint.ApprovedForDraft = true;
                    blueprint.UpdatedAtUtc = DateTime.UtcNow;
                    session.PlanJson = SerializeMemory(WithCurrentDraftBlueprint(memory, blueprint));

                    var summary = createdJobs.Count == 1
                        ? $"Превратила согласованное условие в полноценный draft-черновик. Поставила 1 job в очередь ({createdJobs[0].Id})."
                        : $"Превратила согласованные условия в полноценные draft-черновики. Поставила в очередь {createdJobs.Count} отдельных job без batch.";
                    return new AiFoundryChatToolResultDto
                    {
                        Status = "done",
                        Summary = summary,
                        CourseId = courseId,
                        JobId = createdJobs.Count == 1 ? createdJobs[0].Id : null,
                        NavigateTo = "/admin/ai/chat?sessionId=" + session.Id,
                    };
                }

                case "queue_generate_from_text":
                {
                    var courseId = ReadGuid(args, "courseId") ?? session.CourseId;
                    if (!courseId.HasValue)
                        return FailTool("Нужно выбрать courseId, прежде чем запускать генерацию.");

                    var memory = DeserializeMemory(session.PlanJson);
                    var useCurrentBlueprint = ReadBool(args, "useCurrentBlueprint") ?? false;
                    var proposalId = ReadGuid(args, "proposalId");
                    var blueprint = memory.CurrentDraftBlueprint;
                    var selectedProposal = (useCurrentBlueprint || proposalId.HasValue) && blueprint != null && blueprint.Proposals.Count > 0
                        ? (proposalId.HasValue
                            ? blueprint.Proposals.FirstOrDefault(x => x.Id == proposalId.Value)
                            : blueprint.Proposals.FirstOrDefault())
                        : null;

                    var assignmentType = ReadString(args, "assignmentType")
                        ?? (string.IsNullOrWhiteSpace(selectedProposal?.AssignmentType) ? null : selectedProposal!.AssignmentType)
                        ?? "code-test";
                    var prompt = ReadString(args, "prompt")
                        ?? (selectedProposal != null ? BuildPromptFromChatBlueprintProposal(selectedProposal, memory) : null)
                        ?? BuildFallbackPrompt(messages);
                    var sourceText = ReadString(args, "sourceText")
                        ?? (selectedProposal != null ? BuildSourceTextFromChatBlueprintProposal(selectedProposal, memory) : null)
                        ?? BuildSourceTextFromRecentAttachments(messages);
                    var titleHint = ReadString(args, "titleHint") ?? selectedProposal?.Title;
                    var difficulty = Math.Clamp(ReadInt(args, "difficulty") ?? selectedProposal?.Difficulty ?? 2, 1, 5);
                    var requestedCount = Math.Clamp(ReadInt(args, "count") ?? 1, 1, 50);
                    var notes = ReadString(args, "notes")
                        ?? (selectedProposal != null && blueprint != null ? $"Generated directly from chat blueprint session {session.Id}. Revision {blueprint.Revision}." : null);
                    var structuredContextJson = selectedProposal != null && blueprint != null
                        ? BuildStructuredContextFromChatBlueprintProposal(selectedProposal, blueprint, session.Id)
                        : null;

                    prompt = RewritePromptForStepByStepSeries(prompt, memory, requestedCount);
                    sourceText = RewriteSourceTextForStepByStepSeries(sourceText, memory, requestedCount);
                    titleHint = AiGenerationScenarioPromptAdapter.SuggestTitleHint(memory, prompt, sourceText, requestedCount, titleHint);

                    var directBatchMode = DetermineDirectGenerationMode(memory, prompt, sourceText, requestedCount);
                    if (string.IsNullOrWhiteSpace(structuredContextJson))
                    {
                        var directArgs = new JsonObject
                        {
                            ["notes"] = notes,
                            ["mode"] = directBatchMode,
                            ["sourceText"] = sourceText,
                        };
                        structuredContextJson = BuildStructuredBatchContextJson(session, messages, memory, courseId.Value, prompt, directArgs, directBatchMode, requestedCount, difficulty);
                    }

                    return await EnqueueDirectTextGenerationAsync(
                        session,
                        messages,
                        courseId.Value,
                        assignmentType,
                        prompt,
                        sourceText,
                        titleHint,
                        difficulty,
                        requestedCount,
                        notes,
                        structuredContextJson,
                        Math.Clamp(ReadInt(args, "priority") ?? 20, 1, 100),
                        ReadBool(args, "enableSelfCheck") ?? true,
                        ClampInstructionStrictness(ReadInt(args, "instructionStrictness"), memory.InstructionStrictness),
                        memory.LatestExplicitInstruction,
                        memory.LatestTeachingScript,
                        createdByUserId,
                        createdByDisplayName,
                        ct,
                        selectedProposal,
                        blueprint,
                        selectedProposal != null
                            ? "Поставил в очередь генерацию задания по уже согласованному условию из чата."
                            : "Поставил в очередь генерацию задания из текста.",
                        "Поставил в очередь прямую генерацию {0} задач отдельными job без batch."
                    );
                }
                case "revise_draft_from_chat":
                {
                    var draftId = ReadGuid(args, "draftId");
                    if (!draftId.HasValue)
                        return FailTool("Нужен draftId, чтобы поправить уже готовый AI-черновик.");

                    var memory = DeserializeMemory(session.PlanJson);
                    var prompt = ReadString(args, "prompt") ?? BuildFallbackPrompt(messages);
                    var job = await _jobs.QueueReviseDraftFromChatAsync(new AiReviseDraftFromChatRequestDto
                    {
                        DraftId = draftId.Value,
                        Prompt = prompt,
                        Priority = Math.Clamp(ReadInt(args, "priority") ?? 20, 1, 100),
                        InstructionStrictness = ClampInstructionStrictness(ReadInt(args, "instructionStrictness"), memory.InstructionStrictness),
                        UserInstructionSnapshot = memory.LatestExplicitInstruction,
                        TeachingScript = memory.LatestTeachingScript,
                        ChatSessionId = session.Id,
                    }, createdByUserId, createdByDisplayName, ct);

                    if (job == null)
                        return FailTool("Не удалось найти AI-черновик для правки. Проверь draftId или открой нужный draft заново.");

                    return new AiFoundryChatToolResultDto
                    {
                        Status = "done",
                        Summary = "Поставил в очередь правку готового AI-черновика по новым замечаниям из чата.",
                        NavigateTo = "/admin/ai",
                        JobId = job.Id,
                        CourseId = ReadGuid(args, "courseId") ?? session.CourseId,
                    };
                }
                case "queue_generate_from_file":
                {
                    var courseId = ReadGuid(args, "courseId") ?? session.CourseId;
                    if (!courseId.HasValue)
                        return FailTool("Нужно выбрать courseId, прежде чем запускать генерацию из файла.");

                    var attachment = ResolveAttachment(messages, ReadString(args, "fileKey"));
                    if (attachment == null)
                        return FailTool("Не удалось найти файл для генерации. Прикрепите файл ещё раз или укажите fileKey.");

                    var job = await _jobs.QueueGenerateAssignmentFromFileAsync(new AiGenerateAssignmentFromFileRequestDto
                    {
                        CourseId = courseId.Value,
                        AssignmentType = ReadString(args, "assignmentType") ?? "code-test",
                        Prompt = ReadString(args, "prompt") ?? BuildFallbackPrompt(messages),
                        FileKey = attachment.FileKey,
                        OriginalName = attachment.OriginalName,
                        MimeType = attachment.MimeType,
                        PublicUrl = attachment.PublicUrl,
                        TitleHint = ReadString(args, "titleHint"),
                        Difficulty = Math.Clamp(ReadInt(args, "difficulty") ?? 2, 1, 5),
                        Count = Math.Clamp(ReadInt(args, "count") ?? 1, 1, 50),
                        Notes = ReadString(args, "notes"),
                        Priority = Math.Clamp(ReadInt(args, "priority") ?? 20, 1, 100),
                        EnableSelfCheck = ReadBool(args, "enableSelfCheck") ?? true,
                        InstructionStrictness = ClampInstructionStrictness(ReadInt(args, "instructionStrictness"), DeserializeMemory(session.PlanJson).InstructionStrictness),
                        UserInstructionSnapshot = DeserializeMemory(session.PlanJson).LatestExplicitInstruction,
                        TeachingScript = DeserializeMemory(session.PlanJson).LatestTeachingScript,
                        ChatSessionId = session.Id,
                    }, createdByUserId, createdByDisplayName, ct);

                    return new AiFoundryChatToolResultDto
                    {
                        Status = "done",
                        Summary = $"Поставил в очередь генерацию из файла «{attachment.OriginalName ?? attachment.FileKey}».",
                        NavigateTo = "/admin/ai",
                        JobId = job.Id,
                        CourseId = courseId,
                    };
                }
                case "queue_validate_draft":
                {
                    var draftId = ReadGuid(args, "draftId");
                    if (!draftId.HasValue)
                        return FailTool("Для валидации черновика нужен draftId.");

                    var job = await _jobs.QueueValidateDraftAsync(draftId.Value, new ValidateAiDraftRequestDto
                    {
                        UsePythonSelfCheck = ReadBool(args, "usePythonSelfCheck") ?? true,
                        Prompt = ReadString(args, "prompt"),
                        Priority = Math.Clamp(ReadInt(args, "priority") ?? 15, 1, 100),
                    }, createdByUserId, createdByDisplayName, ct);

                    if (job == null)
                        return FailTool("Черновик для валидации не найден.");

                    return new AiFoundryChatToolResultDto
                    {
                        Status = "done",
                        Summary = "Поставил self-check/валидацию черновика в очередь.",
                        NavigateTo = "/admin/ai",
                        JobId = job.Id,
                        DraftId = draftId,
                    };
                }
                case "approve_draft":
                case "reject_draft":
                {
                    if (!(ReadBool(args, "confirmed") ?? false))
                        return ConfirmationRequired(toolCall, "Для изменения статуса черновика нужно явное подтверждение.");

                    var draftId = ReadGuid(args, "draftId");
                    if (!draftId.HasValue)
                        return FailTool("Для изменения статуса черновика нужен draftId.");

                    var ok = await _jobs.ReviewDraftAsync(draftId.Value, createdByUserId ?? session.CreatedByUserId ?? Guid.Empty, action == "approve_draft" ? "approve" : "reject", ct);
                    if (!ok)
                        return FailTool("Черновик не найден.");

                    return new AiFoundryChatToolResultDto
                    {
                        Status = "done",
                        Summary = action == "approve_draft" ? "Черновик одобрен." : "Черновик отклонён.",
                        NavigateTo = "/admin/ai",
                        DraftId = draftId,
                    };
                }
                case "publish_draft":
                {
                    if (!(ReadBool(args, "confirmed") ?? false))
                        return ConfirmationRequired(toolCall, "Для публикации черновика нужно явное подтверждение.");

                    var draftId = ReadGuid(args, "draftId");
                    if (!draftId.HasValue)
                        return FailTool("Для публикации нужен draftId.");

                    var result = await _jobs.PublishDraftAsync(draftId.Value, createdByUserId ?? session.CreatedByUserId ?? Guid.Empty, new PublishAiDraftRequestDto
                    {
                        CourseId = ReadGuid(args, "courseId") ?? session.CourseId,
                        TitleOverride = ReadString(args, "titleOverride"),
                        Difficulty = ReadInt(args, "difficulty"),
                        Rating = null,
                        Tags = ReadString(args, "tags"),
                        Sort = ReadInt(args, "sort"),
                        AfterAssignmentId = ReadGuid(args, "afterAssignmentId"),
                        ForceWithoutPassedSelfCheck = ReadBool(args, "forceWithoutPassedSelfCheck") ?? false,
                    }, ct);

                    if (result == null)
                        return FailTool("Черновик для публикации не найден.");

                    return new AiFoundryChatToolResultDto
                    {
                        Status = "done",
                        Summary = $"Черновик опубликован как задание «{result.Title}».",
                        NavigateTo = $"/admin/assignments/{result.AssignmentId}/insights",
                        DraftId = result.DraftId,
                        AssignmentId = result.AssignmentId,
                        CourseId = result.CourseId,
                    };
                }
                case "monitor_generation_jobs":
                {
                    var result = await BuildGenerationUpdateToolResultAsync(session, ct, force: true, limit: Math.Clamp(ReadInt(args, "limit") ?? 4, 1, 8));
                    if (result == null)
                        return new AiFoundryChatToolResultDto
                        {
                            Status = "done",
                            Summary = "Пока не появилось новых завершённых черновиков по этой чат-сессии. Можно немного подождать или попросить показать уже существующие draft-черновики.",
                            CourseId = session.CourseId,
                            NavigateTo = "/admin/ai/chat?sessionId=" + session.Id,
                        };
                    return result;
                }
                case "queue_analyze_assignment":
                {
                    var assignmentId = ReadGuid(args, "assignmentId");
                    if (!assignmentId.HasValue)
                        return FailTool("Для анализа задания нужен assignmentId.");

                    var job = await _jobs.QueueAnalyzeAssignmentAsync(new AiAnalyzeAssignmentRequestDto
                    {
                        AssignmentId = assignmentId.Value,
                        IncludeAttempts = ReadBool(args, "includeAttempts") ?? true,
                        IncludeStats = ReadBool(args, "includeStats") ?? true,
                        Prompt = ReadString(args, "prompt"),
                        Priority = Math.Clamp(ReadInt(args, "priority") ?? 10, 1, 100),
                    }, createdByUserId, createdByDisplayName, ct);

                    if (job == null)
                        return FailTool("Задание для анализа не найдено.");

                    return new AiFoundryChatToolResultDto
                    {
                        Status = "done",
                        Summary = "Поставил анализ задания в очередь.",
                        NavigateTo = $"/admin/assignments/{assignmentId.Value}/insights",
                        JobId = job.Id,
                        AssignmentId = assignmentId,
                    };
                }
                case "queue_review_submission":
                {
                    var sourceAttemptId = ReadGuid(args, "sourceAttemptId");
                    if (!sourceAttemptId.HasValue)
                        return FailTool("Для AI-review попытки нужен sourceAttemptId.");

                    var sourceType = ReadString(args, "sourceType") ?? "math";
                    var job = await _jobs.QueueReviewSubmissionAsync(new AiReviewSubmissionRequestDto
                    {
                        SourceType = sourceType,
                        SourceAttemptId = sourceAttemptId.Value,
                        Prompt = ReadString(args, "prompt"),
                        Priority = Math.Clamp(ReadInt(args, "priority") ?? 10, 1, 100),
                    }, createdByUserId, createdByDisplayName, ct);

                    if (job == null)
                        return FailTool("Попытка для AI-review не найдена.");

                    return new AiFoundryChatToolResultDto
                    {
                        Status = "done",
                        Summary = $"Поставил AI-review попытки ({sourceType}) в очередь.",
                        NavigateTo = "/admin/ai",
                        JobId = job.Id,
                    };
                }
                case "queue_review_user":
                {
                    var targetUserId = ReadGuid(args, "userId");
                    if (!targetUserId.HasValue)
                        return FailTool("Для AI-review пользователя нужен userId.");

                    var job = await _jobs.QueueReviewUserAsync(new AiReviewUserRequestDto
                    {
                        UserId = targetUserId.Value,
                        IncludeSupport = ReadBool(args, "includeSupport") ?? true,
                        IncludeMinecraft = ReadBool(args, "includeMinecraft") ?? true,
                        IncludeRecentAttempts = ReadBool(args, "includeRecentAttempts") ?? true,
                        Prompt = ReadString(args, "prompt"),
                        Priority = Math.Clamp(ReadInt(args, "priority") ?? 10, 1, 100),
                    }, createdByUserId, createdByDisplayName, ct);

                    if (job == null)
                        return FailTool("Пользователь для AI-review не найден.");

                    return new AiFoundryChatToolResultDto
                    {
                        Status = "done",
                        Summary = "Поставил AI-review пользователя в очередь.",
                        NavigateTo = "/admin/ai",
                        JobId = job.Id,
                    };
                }

                // ── show_draft: Display full draft content inline in chat ──────────
                case "show_draft":
                {
                    var draftId = ReadGuid(args, "draftId");
                    if (!draftId.HasValue)
                        return FailTool("Для просмотра черновика нужен draftId.");

                    var draft = await _db.AiGeneratedAssignmentDrafts
                        .AsNoTracking()
                        .Where(d => d.Id == draftId.Value)
                        .Select(d => new { d.Id, d.Title, d.AssignmentType, d.Status, d.DraftJson, d.BatchItemId })
                        .FirstOrDefaultAsync(ct);
                    if (draft == null)
                        return FailTool("Черновик не найден.");

                    // Parse DraftJson to extract key fields for display
                    var sb = new System.Text.StringBuilder();
                    sb.AppendLine($"📄 **{draft.Title}** (тип: {draft.AssignmentType}, статус: {draft.Status})");
                    sb.AppendLine();

                    try
                    {
                        using var doc = JsonDocument.Parse(draft.DraftJson);
                        var root = doc.RootElement;

                        if (root.TryGetProperty("description", out var desc) && desc.ValueKind == JsonValueKind.String)
                        {
                            var descText = desc.GetString() ?? "";
                            sb.AppendLine("**Условие:**");
                            sb.AppendLine(descText.Length > 2000 ? descText[..2000] + "…" : descText);
                            sb.AppendLine();
                        }

                        if (root.TryGetProperty("initialCode", out var init) && init.ValueKind == JsonValueKind.String)
                        {
                            var initText = init.GetString() ?? "";
                            if (!string.IsNullOrWhiteSpace(initText))
                            {
                                sb.AppendLine("**Начальный код:**");
                                sb.AppendLine($"```\n{(initText.Length > 1000 ? initText[..1000] + "…" : initText)}\n```");
                                sb.AppendLine();
                            }
                        }

                        if (root.TryGetProperty("solution", out var sol) && sol.ValueKind == JsonValueKind.String)
                        {
                            var solText = sol.GetString() ?? "";
                            sb.AppendLine("**Эталонное решение:**");
                            sb.AppendLine($"```\n{(solText.Length > 1500 ? solText[..1500] + "…" : solText)}\n```");
                            sb.AppendLine();
                        }

                        // Test cases
                        if (root.TryGetProperty("testCases", out var tests) && tests.ValueKind == JsonValueKind.Array)
                        {
                            var testCount = tests.GetArrayLength();
                            sb.AppendLine($"**Тесты ({testCount}):**");
                            int shown = 0;
                            foreach (var tc in tests.EnumerateArray())
                            {
                                if (shown >= 5) { sb.AppendLine($"… и ещё {testCount - 5}"); break; }
                                var inp = tc.TryGetProperty("input", out var i) ? i.ToString() : "—";
                                var exp = tc.TryGetProperty("expectedOutput", out var e) ? e.ToString() : "—";
                                sb.AppendLine($"  [{shown + 1}] input: {Truncate(inp, 120)} → expected: {Truncate(exp, 120)}");
                                shown++;
                            }
                            sb.AppendLine();
                        }

                        // Hints
                        if (root.TryGetProperty("hints", out var hints) && hints.ValueKind == JsonValueKind.Array)
                        {
                            var hintList = new List<string>();
                            foreach (var h in hints.EnumerateArray())
                                hintList.Add(h.ValueKind == JsonValueKind.String ? h.GetString()! : h.ToString());
                            if (hintList.Count > 0)
                                sb.AppendLine($"**Подсказки:** {string.Join(" / ", hintList.Take(5))}");
                        }
                    }
                    catch
                    {
                        sb.AppendLine("(Не удалось разобрать содержимое черновика)");
                    }

                    return new AiFoundryChatToolResultDto
                    {
                        Status = "done",
                        Summary = sb.ToString(),
                        DraftId = draft.Id,
                    };
                }

                // ── show_draft_reviews: Display scorecard + review results ──────────
                case "show_draft_reviews":
                {
                    var draftId = ReadGuid(args, "draftId");
                    if (!draftId.HasValue)
                        return FailTool("Для просмотра оценок нужен draftId.");

                    var item = await _db.AiBatchItems
                        .AsNoTracking()
                        .Where(i => i.DraftId == draftId.Value)
                        .Select(i => new { i.Id, i.Status, i.ScorecardJson, i.BriefReviewJson, i.RepairCount, i.Index,
                            DraftTitle = i.Draft != null ? i.Draft.Title : null })
                        .FirstOrDefaultAsync(ct);

                    if (item == null)
                    {
                        // Fallback: maybe draft exists but not linked to a batch item — just return minimal info
                        var standalone = await _db.AiGeneratedAssignmentDrafts.AsNoTracking()
                            .Where(d => d.Id == draftId.Value)
                            .Select(d => new { d.Title, d.Status })
                            .FirstOrDefaultAsync(ct);
                        if (standalone == null)
                            return FailTool("Черновик не найден.");
                        return new AiFoundryChatToolResultDto
                        {
                            Status = "done",
                            Summary = $"📄 {standalone.Title} — статус: {standalone.Status}. Этот черновик не привязан к batch-pipeline, поэтому детальной оценки нет.",
                            DraftId = draftId,
                        };
                    }

                    var rsb = new System.Text.StringBuilder();
                    rsb.AppendLine($"📊 **Оценка черновика** #{item.Index + 1}: {item.DraftTitle ?? "(без названия)"} (статус: {item.Status}, ремонтов: {item.RepairCount})");
                    rsb.AppendLine();

                    if (!string.IsNullOrWhiteSpace(item.ScorecardJson))
                    {
                        try
                        {
                            using var sdoc = JsonDocument.Parse(item.ScorecardJson);
                            var sr = sdoc.RootElement;
                            if (sr.TryGetProperty("band", out var band))
                                rsb.AppendLine($"**Band:** {band}");
                            if (sr.TryGetProperty("overallScore", out var os) && os.TryGetInt32(out var osVal))
                                rsb.AppendLine($"**Общая оценка:** {osVal}/100");
                            if (sr.TryGetProperty("dimensions", out var dims) && dims.ValueKind == JsonValueKind.Object)
                            {
                                rsb.AppendLine("**Измерения:**");
                                foreach (var prop in dims.EnumerateObject())
                                {
                                    if (prop.Value.ValueKind == JsonValueKind.Object)
                                    {
                                        var dimScore = prop.Value.TryGetProperty("score", out var ds) && ds.TryGetInt32(out var dsv) ? dsv : (int?)null;
                                        var dimIssues = prop.Value.TryGetProperty("issues", out var di) && di.ValueKind == JsonValueKind.Array ? di.GetArrayLength() : 0;
                                        rsb.AppendLine($"  • {prop.Name}: {dimScore?.ToString() ?? "—"}/100{(dimIssues > 0 ? $" ({dimIssues} замечаний)" : "")}");
                                    }
                                }
                            }
                            if (sr.TryGetProperty("repairPlan", out var rp) && rp.ValueKind == JsonValueKind.Object)
                            {
                                if (rp.TryGetProperty("primaryRoute", out var pr))
                                    rsb.AppendLine($"**План ремонта:** {pr}");
                            }
                        }
                        catch { rsb.AppendLine("(Не удалось разобрать scorecard)"); }
                    }
                    else
                    {
                        rsb.AppendLine("Scorecard ещё не готов (проверки не завершены).");
                    }

                    return new AiFoundryChatToolResultDto
                    {
                        Status = "done",
                        Summary = rsb.ToString(),
                        DraftId = draftId,
                    };
                }

                // ── cancel_batch: Cancel/delete a batch from chat ───────────────────
                case "cancel_batch":
                {
                    if (!(ReadBool(args, "confirmed") ?? false))
                        return ConfirmationRequired(toolCall, "Для отмены batch нужно явное подтверждение.");

                    var batchId = ReadGuid(args, "batchId");
                    if (!batchId.HasValue)
                        return FailTool("Для отмены batch нужен batchId.");

                    var ok = await _jobs.DeleteBatchAsync(batchId.Value, ct);
                    if (!ok)
                        return FailTool("Batch не найден.");

                    return new AiFoundryChatToolResultDto
                    {
                        Status = "done",
                        Summary = $"Batch {batchId.Value} удалён вместе с черновиками.",
                    };
                }

                // ── publish_batch: Bulk-publish all approved drafts from a batch ────
                case "publish_batch":
                {
                    if (!(ReadBool(args, "confirmed") ?? false))
                        return ConfirmationRequired(toolCall, "Для массовой публикации batch нужно явное подтверждение.");

                    var batchId = ReadGuid(args, "batchId");
                    if (!batchId.HasValue)
                        return FailTool("Для массовой публикации нужен batchId.");

                    var batch = await _db.AiBatches.AsNoTracking()
                        .Where(b => b.Id == batchId.Value)
                        .Select(b => new { b.Id, b.CourseId })
                        .FirstOrDefaultAsync(ct);
                    if (batch == null)
                        return FailTool("Batch не найден.");

                    var draftsToPublish = await _db.AiBatchItems
                        .AsNoTracking()
                        .Where(i => i.BatchId == batchId.Value && i.DraftId.HasValue
                            && (i.Status == "ready" || i.Status == "reviewed" || i.Status == "approved"))
                        .OrderBy(i => i.Index)
                        .Select(i => new { i.DraftId, i.Draft!.Title })
                        .ToListAsync(ct);

                    if (draftsToPublish.Count == 0)
                        return FailTool("В этом batch нет готовых к публикации черновиков (нужен статус ready/reviewed/approved).");

                    var forcePublish = ReadBool(args, "forceWithoutPassedSelfCheck") ?? false;
                    var published = new List<string>();
                    var failed = new List<string>();

                    foreach (var d in draftsToPublish)
                    {
                        try
                        {
                            var result = await _jobs.PublishDraftAsync(d.DraftId!.Value,
                                createdByUserId ?? session.CreatedByUserId ?? Guid.Empty,
                                new PublishAiDraftRequestDto
                                {
                                    CourseId = ReadGuid(args, "courseId") ?? batch.CourseId ?? session.CourseId,
                                    ForceWithoutPassedSelfCheck = forcePublish,
                                }, ct);
                            if (result != null)
                                published.Add(result.Title);
                            else
                                failed.Add(d.Title ?? d.DraftId.ToString()!);
                        }
                        catch
                        {
                            failed.Add(d.Title ?? d.DraftId.ToString()!);
                        }
                    }

                    var psb = new System.Text.StringBuilder();
                    psb.AppendLine($"📦 Массовая публикация batch:");
                    if (published.Count > 0)
                        psb.AppendLine($"✅ Опубликовано ({published.Count}): {string.Join(", ", published.Select(t => $"«{t}»"))}");
                    if (failed.Count > 0)
                        psb.AppendLine($"❌ Не удалось ({failed.Count}): {string.Join(", ", failed.Select(t => $"«{t}»"))}");
                    if (published.Count == 0 && failed.Count == 0)
                        psb.AppendLine("Ничего не опубликовано.");

                    return new AiFoundryChatToolResultDto
                    {
                        Status = failed.Count > 0 ? "partial" : "done",
                        Summary = psb.ToString(),
                        BatchId = batchId,
                    };
                }

                default:
                    return FailTool($"Неизвестное действие AI: {toolCall.Name}");
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "AI chat tool execution failed: {ToolName}", toolCall.Name);
            return FailTool($"Не удалось выполнить действие «{toolCall.Name}»: {ex.Message}");
        }
    }

    private async Task<object> BuildChatPayloadAsync(AiFoundryChatSession session, List<AiFoundryChatMessageDto> messages, string actionMode, int? instructionStrictness, AiEnsureCourseAssignmentOverviewsResultDto? autoOverviewBootstrap, CancellationToken ct)
    {
        var courses = await _db.Courses.AsNoTracking()
            .OrderBy(x => x.Title)
            .Select(x => new
            {
                id = x.Id,
                title = x.Title,
                description = x.Description,
                isPublic = x.IsPublic,
            })
            .Take(100)
            .ToListAsync(ct);

        var selectedCourse = session.CourseId == null
            ? null
            : await _db.Courses.AsNoTracking()
                .Where(x => x.Id == session.CourseId.Value)
                .Select(x => new
                {
                    id = x.Id,
                    title = x.Title,
                    description = x.Description,
                    isPublic = x.IsPublic,
                    assignmentCount = x.Assignments.Count(),
                })
                .FirstOrDefaultAsync(ct);

        var recentAssignmentsQuery = _db.TaskAssignments.AsNoTracking().AsQueryable();
        if (session.CourseId.HasValue)
            recentAssignmentsQuery = recentAssignmentsQuery.Where(x => x.CourseId == session.CourseId.Value);

        var recentAssignmentRows = await recentAssignmentsQuery
            .OrderByDescending(x => x.UpdatedAt)
            .Take(12)
            .Select(x => new
            {
                id = x.Id,
                courseId = x.CourseId,
                title = x.Title,
                type = x.Type,
                difficulty = x.Difficulty,
                updatedAtUtc = x.UpdatedAt,
            })
            .ToListAsync(ct);
        var recentAssignmentOverviewMap = await LoadLatestAssignmentOverviewMapAsync(recentAssignmentRows.Select(x => x.id), ct);
        var recentAssignments = recentAssignmentRows
            .Select(x => new
            {
                x.id,
                x.courseId,
                x.title,
                x.type,
                x.difficulty,
                x.updatedAtUtc,
                latestAiOverview = recentAssignmentOverviewMap.TryGetValue(x.id, out var overview) ? overview : null,
            })
            .ToList();

        AiCourseOverviewCoverageDto? courseOverviewCoverage = null;
        List<AiCourseLandmarkAssignmentDto> landmarkAssignments = new();
        if (session.CourseId.HasValue)
        {
            courseOverviewCoverage = await BuildCourseOverviewCoverageAsync(session.CourseId.Value, ct);
            landmarkAssignments = await LoadCourseLandmarkAssignmentsAsync(session.CourseId.Value, 8, ct);
        }

        var recentDraftsQuery = _db.AiGeneratedAssignmentDrafts.AsNoTracking().AsQueryable();
        if (session.CourseId.HasValue)
            recentDraftsQuery = recentDraftsQuery.Where(x => x.CourseId == session.CourseId.Value);

        var recentDrafts = await recentDraftsQuery
            .OrderByDescending(x => x.UpdatedAtUtc)
            .Take(12)
            .Select(x => new
            {
                id = x.Id,
                courseId = x.CourseId,
                batchId = x.BatchId,
                jobId = x.JobId,
                title = x.Title,
                assignmentType = x.AssignmentType,
                status = x.Status,
                updatedAtUtc = x.UpdatedAtUtc,
            })
            .ToListAsync(ct);

        var sessionRecentDrafts = await LoadSessionRecentDraftsAsync(session, ct);

        var recentBatchesQuery = _db.AiBatches.AsNoTracking().AsQueryable();
        if (session.CourseId.HasValue)
            recentBatchesQuery = recentBatchesQuery.Where(x => x.CourseId == session.CourseId.Value);

        var recentBatches = await recentBatchesQuery
            .OrderByDescending(x => x.UpdatedAtUtc)
            .Take(8)
            .Select(x => new
            {
                id = x.Id,
                courseId = x.CourseId,
                prompt = x.Prompt,
                assignmentType = x.AssignmentType,
                mode = x.Mode,
                requestedCount = x.RequestedCount,
                status = x.Status,
                currentStage = x.CurrentStage,
                updatedAtUtc = x.UpdatedAtUtc,
            })
            .ToListAsync(ct);

        var recentJobsQuery = _db.AiJobs.AsNoTracking().AsQueryable();
        if (session.CourseId.HasValue)
            recentJobsQuery = recentJobsQuery.Where(x => x.CourseId == session.CourseId.Value);
        else if (session.CreatedByUserId.HasValue)
            recentJobsQuery = recentJobsQuery.Where(x => x.CreatedByUserId == session.CreatedByUserId.Value);

        var recentJobs = await recentJobsQuery
            .OrderByDescending(x => x.CreatedAtUtc)
            .Take(12)
            .Select(x => new
            {
                id = x.Id,
                type = x.Type,
                status = x.Status,
                targetEntityType = x.TargetEntityType,
                targetEntityId = x.TargetEntityId,
                courseId = x.CourseId,
                stageCode = x.StageCode,
                stageLabel = x.StageLabel,
                priority = x.Priority,
                createdAtUtc = x.CreatedAtUtc,
                completedAtUtc = x.CompletedAtUtc,
                errorText = x.ErrorText,
            })
            .ToListAsync(ct);

        var sessionRecentJobs = await LoadSessionRecentJobsAsync(session, ct);

        var recentUsers = await _db.Users.AsNoTracking()
            .OrderByDescending(x => x.LastLoginAt ?? x.UpdatedAt)
            .Take(16)
            .Select(x => new
            {
                id = x.Id,
                email = x.Email,
                displayName = (x.FirstName + " " + x.LastName).Trim(),
                role = x.Role,
                lastLoginAtUtc = x.LastLoginAt,
                minecraftNick = x.MinecraftNick,
                telegramUsername = x.TelegramUsername,
            })
            .ToListAsync(ct);

        var recentTestAttemptsQuery = _db.UserTaskTestAttempts.AsNoTracking().AsQueryable();
        if (session.CourseId.HasValue)
            recentTestAttemptsQuery = recentTestAttemptsQuery.Where(x => x.TaskAssignment != null && x.TaskAssignment.CourseId == session.CourseId.Value);

        var recentTestAttempts = await recentTestAttemptsQuery
            .OrderByDescending(x => x.SubmittedAt ?? x.CreatedAt)
            .Take(10)
            .Select(x => new
            {
                sourceType = "test",
                sourceAttemptId = x.Id,
                assignmentId = x.TaskAssignmentId,
                assignmentTitle = x.TaskAssignment != null ? x.TaskAssignment.Title : null,
                userId = x.UserId,
                userDisplayName = x.User != null ? ((x.User.FirstName + " " + x.User.LastName).Trim()) : null,
                scorePercent = x.ScorePercent,
                passed = x.Passed,
                submittedAtUtc = x.SubmittedAt,
                createdAtUtc = x.CreatedAt,
            })
            .ToListAsync(ct);

        var recentMathAttemptsQuery = _db.UserTaskMathAttempts.AsNoTracking().AsQueryable();
        if (session.CourseId.HasValue)
            recentMathAttemptsQuery = recentMathAttemptsQuery.Where(x => x.TaskAssignment != null && x.TaskAssignment.CourseId == session.CourseId.Value);

        var recentMathAttempts = await recentMathAttemptsQuery
            .OrderByDescending(x => x.SubmittedAt ?? x.CreatedAt)
            .Take(10)
            .Select(x => new
            {
                sourceType = "math",
                sourceAttemptId = x.Id,
                assignmentId = x.TaskAssignmentId,
                assignmentTitle = x.TaskAssignment != null ? x.TaskAssignment.Title : null,
                userId = x.UserId,
                userDisplayName = x.User != null ? ((x.User.FirstName + " " + x.User.LastName).Trim()) : null,
                scorePercent = x.ScorePercent,
                passed = x.Passed,
                submittedAtUtc = x.SubmittedAt,
                createdAtUtc = x.CreatedAt,
            })
            .ToListAsync(ct);

        var recentAttempts = recentTestAttempts.Cast<object>()
            .Concat(recentMathAttempts.Cast<object>())
            .Take(16)
            .ToList();

        var attachmentDigest = messages
            .Where(x => string.Equals(x.Role, "user", StringComparison.OrdinalIgnoreCase))
            .SelectMany(x => x.Attachments ?? new List<AiFoundryChatAttachmentDto>())
            .Where(x => !string.IsNullOrWhiteSpace(x.FileKey))
            .GroupBy(x => x.FileKey, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Last())
            .TakeLast(10)
            .Select(x => new
            {
                fileKey = x.FileKey,
                originalName = x.OriginalName,
                mimeType = x.MimeType,
                hasTextExcerpt = !string.IsNullOrWhiteSpace(x.TextExcerpt),
                sizeBytes = x.SizeBytes,
            })
            .ToList();

        var memory = BuildMemory(messages, session.PlanJson);

        var conversation = messages.TakeLast(18).Select(x => new
        {
            id = x.Id,
            role = x.Role,
            content = x.Content,
            createdAtUtc = x.CreatedAtUtc,
            status = x.Status,
            attachments = x.Attachments.Select(a => new
            {
                fileKey = a.FileKey,
                originalName = a.OriginalName,
                mimeType = a.MimeType,
                publicUrl = a.PublicUrl,
                sizeBytes = a.SizeBytes,
                textExcerpt = a.TextExcerpt,
            }).ToList(),
            toolCalls = (x.ToolCalls?.Count > 0 ? x.ToolCalls : (x.ToolCall == null ? new List<AiFoundryChatToolCallDto>() : new List<AiFoundryChatToolCallDto> { x.ToolCall }))
                .Select(tc => new
                {
                    name = tc.Name,
                    reason = tc.Reason,
                    argumentsJson = tc.ArgumentsJson,
                }).ToList(),
            toolResults = (x.ToolResults?.Count > 0 ? x.ToolResults : (x.ToolResult == null ? new List<AiFoundryChatToolResultDto>() : new List<AiFoundryChatToolResultDto> { x.ToolResult }))
                .Select(tr => new
                {
                    status = tr.Status,
                    summary = tr.Summary,
                    navigateTo = tr.NavigateTo,
                    jobId = tr.JobId,
                    batchId = tr.BatchId,
                    draftId = tr.DraftId,
                    assignmentId = tr.AssignmentId,
                    courseId = tr.CourseId,
                    requiresConfirmation = tr.RequiresConfirmation,
                    suggestedConfirmationMessage = tr.SuggestedConfirmationMessage,
                    confirmationToolCall = tr.ConfirmationToolCall == null ? null : new
                    {
                        name = tr.ConfirmationToolCall.Name,
                        reason = tr.ConfirmationToolCall.Reason,
                        argumentsJson = tr.ConfirmationToolCall.ArgumentsJson,
                    },
                }).ToList(),
            experimental = x.Experimental == null ? null : new
            {
                mode = x.Experimental.Mode,
                summary = x.Experimental.Summary,
                recommendedKey = x.Experimental.RecommendedKey,
                candidates = x.Experimental.Candidates.Select(c => new
                {
                    key = c.Key,
                    label = c.Label,
                    promptStyle = c.PromptStyle,
                    assistantMessage = c.AssistantMessage,
                    score = c.Score,
                    scoreReason = c.ScoreReason,
                    recommended = c.Recommended,
                    toolCalls = c.ToolCalls.Select(tc => new
                    {
                        name = tc.Name,
                        reason = tc.Reason,
                        argumentsJson = tc.ArgumentsJson,
                    }).ToList(),
                }).ToList(),
            },
        }).ToList();

        return new
        {
            requestType = AiFoundryJobTypes.ChatTurn,
            sessionId = session.Id,
            sessionTitle = session.Title,
            courseId = session.CourseId,
            selectedCourse,
            memory,
            currentDraftBlueprint = memory.CurrentDraftBlueprint,
            instructionStrictness = memory.InstructionStrictness,
            conversation,
            recentAttachments = attachmentDigest,
            recentAssignments,
            courseOverviewCoverage,
            landmarkAssignments,
            autoOverviewBootstrap,
            recentDrafts,
            sessionRecentDrafts,
            recentBatches,
            recentJobs,
            sessionRecentJobs,
            recentUsers,
            recentAttempts,
            availableCourses = courses,
            availableActions = new object[]
            {
                new
                {
                    name = "analyze_course_progression",
                    description = "Изучить текущий курс, найти резкие вводы новых функций/конструкций и предложить мостики с afterAssignmentId.",
                    requiredArguments = new[] { "courseId" },
                    optionalArguments = new[] { "focus", "limitAssignments", "priority" },
                },
                new
                {
                    name = "inspect_course_assignments",
                    description = "Открыть часть курса и показать конкретные задания по порядку, чтобы AI могла сверить стиль, соседние темы и место вставки.",
                    requiredArguments = new[] { "courseId" },
                    optionalArguments = new[] { "query", "aroundAssignmentId", "window", "limitAssignments" },
                },
                new
                {
                    name = "prepare_bridge_plan",
                    description = "Собрать и сохранить подробный план мостиков: что вставлять, после какого assignmentId и в каком стиле названий.",
                    requiredArguments = new[] { "courseId" },
                    optionalArguments = new[] { "focus", "findingIndexes", "afterAssignmentId", "count" },
                },
                new
                {
                    name = "show_bridge_plan",
                    description = "Показать текущий сохранённый план мостиков без пересборки, чтобы обсудить его с админом.",
                    requiredArguments = new[] { "courseId" },
                    optionalArguments = Array.Empty<string>(),
                },
                new
                {
                    name = "revise_bridge_plan",
                    description = "Точечно поправить уже собранный план мостиков: подтвердить/отклонить пункты, поменять taskCount, difficulty, titleHint или afterAssignmentId.",
                    requiredArguments = new[] { "courseId" },
                    optionalArguments = new[] { "itemIndexes", "itemIndex", "taskCount", "difficulty", "titleHint", "afterAssignmentId", "reason", "note", "confirm", "reject" },
                },
                new
                {
                    name = "advance_agent_stage",
                    description = "Продолжить агента по памяти и текущему состоянию: выбрать следующий логичный шаг между аудитом, просмотром заданий, планом мостиков и прямую серию мостиков.",
                    requiredArguments = new[] { "courseId" },
                    optionalArguments = new[] { "focus", "priority" },
                },
                new
                {
                    name = "save_chat_blueprint",
                    description = "Сохранить в памяти чата 1 или несколько примерных условий/набросков задач, чтобы потом обсудить правки и только после одобрения превратить их в полноценные draft-черновики.",
                    requiredArguments = new[] { "courseId", "proposals" },
                    optionalArguments = new[] { "summary", "approvedForDraft", "revision" },
                },
                new
                {
                    name = "revise_chat_blueprint",
                    description = "Обновить уже сохранённые примерные условия из памяти чата по новым замечаниям пользователя, не сбрасывая workflow.",
                    requiredArguments = new[] { "courseId", "proposals" },
                    optionalArguments = new[] { "summary", "approvedForDraft", "revision" },
                },
                new
                {
                    name = "show_chat_blueprint",
                    description = "Показать текущие примерные условия, уже сохранённые в памяти этой чат-сессии, без запуска генерации draft.",
                    requiredArguments = new[] { "courseId" },
                    optionalArguments = Array.Empty<string>(),
                },
                new
                {
                    name = "finalize_chat_blueprint",
                    description = "После явного одобрения пользователя превратить сохранённые примерные условия из чата в полноценные draft-черновики. Не использовать без явной фразы вроде 'одобряю', 'закидывай в черновик', 'делай черновик'.",
                    requiredArguments = new[] { "courseId" },
                    optionalArguments = new[] { "proposalIds", "priority", "enableSelfCheck" },
                },
                new
                {
                    name = "drop_chat_blueprint",
                    description = "Сбросить текущие примерные условия из памяти чата, если пользователь просит начать заново или выбросить старые варианты.",
                    requiredArguments = new[] { "courseId" },
                    optionalArguments = Array.Empty<string>(),
                },
                new
                {
                    name = "queue_generate_from_text",
                    description = "Запустить генерацию одного или нескольких заданий из текста. Если в памяти уже есть согласованный blueprint, можно использовать именно его и не передавать prompt вручную.",
                    requiredArguments = new[] { "courseId" },
                    optionalArguments = new[] { "assignmentType", "prompt", "sourceText", "count", "difficulty", "titleHint", "notes", "priority", "enableSelfCheck", "useCurrentBlueprint", "proposalId" },
                },
                new
                {
                    name = "revise_draft_from_chat",
                    description = "Исправить уже созданный AI-черновик по новым замечаниям пользователя из чата, не создавая новый draft с нуля.",
                    requiredArguments = new[] { "draftId", "prompt" },
                    optionalArguments = new[] { "courseId", "priority", "instructionStrictness" },
                },
                new
                {
                    name = "queue_generate_from_file",
                    description = "Запустить генерацию из прикреплённого файла, используя fileKey последнего вложения.",
                    requiredArguments = new[] { "courseId", "prompt" },
                    optionalArguments = new[] { "fileKey", "assignmentType", "count", "difficulty", "titleHint", "notes", "priority", "enableSelfCheck" },
                },
                new
                {
                    name = "queue_validate_draft",
                    description = "Запустить self-check/валидацию готового AI-черновика.",
                    requiredArguments = new[] { "draftId" },
                    optionalArguments = new[] { "prompt", "priority", "usePythonSelfCheck" },
                },
                new
                {
                    name = "approve_draft",
                    description = "Одобрить AI-черновик. Использовать только если пользователь явно попросил одобрить.",
                    requiredArguments = new[] { "draftId", "confirmed" },
                    optionalArguments = Array.Empty<string>(),
                },
                new
                {
                    name = "reject_draft",
                    description = "Отклонить AI-черновик. Использовать только если пользователь явно попросил отклонить.",
                    requiredArguments = new[] { "draftId", "confirmed" },
                    optionalArguments = Array.Empty<string>(),
                },
                new
                {
                    name = "publish_draft",
                    description = "Опубликовать AI-черновик как реальное задание. Использовать только если пользователь явно подтвердил публикацию.",
                    requiredArguments = new[] { "draftId", "confirmed" },
                    optionalArguments = new[] { "courseId", "titleOverride", "difficulty", "tags", "sort", "afterAssignmentId", "forceWithoutPassedSelfCheck" },
                },
                new
                {
                    name = "monitor_generation_jobs",
                    description = "Проверить, завершились ли generation/revise jobs этой чат-сессии, и показать появившиеся draft-черновики прямо в чате.",
                    requiredArguments = Array.Empty<string>(),
                    optionalArguments = new[] { "jobId", "batchId", "courseId", "limit" },
                },
                new
                {
                    name = "queue_analyze_assignment",
                    description = "Запустить AI-анализ уже существующего задания.",
                    requiredArguments = new[] { "assignmentId" },
                    optionalArguments = new[] { "prompt", "includeStats", "includeAttempts", "priority" },
                },
                new
                {
                    name = "queue_review_submission",
                    description = "Запустить AI-review конкретной попытки пользователя.",
                    requiredArguments = new[] { "sourceType", "sourceAttemptId" },
                    optionalArguments = new[] { "prompt", "priority" },
                },
                new
                {
                    name = "queue_review_user",
                    description = "Запустить AI-risk-review пользователя на основе попыток и интеграций.",
                    requiredArguments = new[] { "userId" },
                    optionalArguments = new[] { "prompt", "priority", "includeSupport", "includeMinecraft", "includeRecentAttempts" },
                },
                new
                {
                    name = "show_draft",
                    description = "Показать содержимое AI-черновика прямо в чате: условие задачи, эталонное решение, тесты, подсказки. Используй когда пользователь хочет посмотреть что сгенерировалось.",
                    requiredArguments = new[] { "draftId" },
                    optionalArguments = Array.Empty<string>(),
                },
                new
                {
                    name = "show_draft_reviews",
                    description = "Показать оценку качества (scorecard) черновика: band, общая оценка, оценки по измерениям (pedagogy, structural, style, runtime, similarity), план ремонта.",
                    requiredArguments = new[] { "draftId" },
                    optionalArguments = Array.Empty<string>(),
                },
                new
                {
                    name = "cancel_batch",
                    description = "Отменить и удалить batch вместе с черновиками. Используй если пользователь явно просит остановить/отменить/удалить batch.",
                    requiredArguments = new[] { "batchId", "confirmed" },
                    optionalArguments = Array.Empty<string>(),
                },
                new
                {
                    name = "publish_batch",
                    description = "Массово опубликовать все готовые черновики из batch (ready/reviewed/approved). Используй если пользователь просит 'опубликуй всё' или 'публикуй batch'.",
                    requiredArguments = new[] { "batchId", "confirmed" },
                    optionalArguments = new[] { "courseId", "forceWithoutPassedSelfCheck" },
                },
            },
            defaults = new
            {
                assignmentType = "code-test",
                difficulty = 2,
                count = 5,
                mode = "topic-pack",
            },
            experimental = new
            {
                enabled = string.Equals(actionMode, "experimental", StringComparison.OrdinalIgnoreCase),
                variantCount = 4,
                mode = "candidate-lab",
                autoExecuteWinner = false,
                profiles = new[] { "inspect-first", "ladder-first", "validator-skeptic", "direct-executor" },
            },
            actionMode,
        };
    }

    private async Task<List<object>> LoadSessionRecentDraftsAsync(AiFoundryChatSession session, CancellationToken ct)
    {
        if (!session.CourseId.HasValue)
            return new List<object>();

        var sessionToken = session.Id.ToString();
        var jobRows = await _db.AiJobs.AsNoTracking()
            .Where(x => x.CourseId == session.CourseId.Value
                && (x.Type == "assignment_generate_from_text" || x.Type == "assignment_generate_from_file" || x.Type == "assignment_repair" || x.Type == "assignment_validate_draft"))
            .OrderByDescending(x => x.CreatedAtUtc)
            .Take(160)
            .Select(x => new { x.Id, x.InputJson })
            .ToListAsync(ct);

        var jobIds = jobRows
            .Where(x => !string.IsNullOrWhiteSpace(x.InputJson) && x.InputJson!.Contains(sessionToken, StringComparison.OrdinalIgnoreCase))
            .Select(x => x.Id)
            .ToHashSet();

        if (jobIds.Count == 0)
            return new List<object>();

        return await _db.AiGeneratedAssignmentDrafts.AsNoTracking()
            .Where(x => x.CourseId == session.CourseId.Value && (jobIds.Contains(x.JobId) || (x.ParentJobId.HasValue && jobIds.Contains(x.ParentJobId.Value))))
            .OrderByDescending(x => x.UpdatedAtUtc)
            .Take(12)
            .Select(x => (object)new
            {
                id = x.Id,
                courseId = x.CourseId,
                batchId = x.BatchId,
                jobId = x.JobId,
                title = x.Title,
                assignmentType = x.AssignmentType,
                status = x.Status,
                updatedAtUtc = x.UpdatedAtUtc,
            })
            .ToListAsync(ct);
    }

    private async Task<List<object>> LoadSessionRecentJobsAsync(AiFoundryChatSession session, CancellationToken ct)
    {
        if (!session.CourseId.HasValue)
            return new List<object>();

        var sessionToken = session.Id.ToString();
        var rows = await _db.AiJobs.AsNoTracking()
            .Where(x => x.CourseId == session.CourseId.Value)
            .OrderByDescending(x => x.CreatedAtUtc)
            .Take(160)
            .Select(x => new
            {
                x.Id,
                x.Type,
                x.Status,
                x.TargetEntityType,
                x.TargetEntityId,
                x.CourseId,
                x.StageCode,
                x.StageLabel,
                x.Priority,
                x.CreatedAtUtc,
                x.CompletedAtUtc,
                x.ErrorText,
                x.InputJson,
            })
            .ToListAsync(ct);

        return rows
            .Where(x => !string.IsNullOrWhiteSpace(x.InputJson) && x.InputJson!.Contains(sessionToken, StringComparison.OrdinalIgnoreCase))
            .Take(12)
            .Select(x => (object)new
            {
                id = x.Id,
                type = x.Type,
                status = x.Status,
                targetEntityType = x.TargetEntityType,
                targetEntityId = x.TargetEntityId,
                courseId = x.CourseId,
                stageCode = x.StageCode,
                stageLabel = x.StageLabel,
                priority = x.Priority,
                createdAtUtc = x.CreatedAtUtc,
                completedAtUtc = x.CompletedAtUtc,
                errorText = x.ErrorText,
            })
            .ToList();
    }

    private static IReadOnlyList<AiFoundryChatToolCallDto> ParseToolCalls(JsonNode? root)
    {
        var list = new List<AiFoundryChatToolCallDto>();
        if (root is JsonObject obj)
        {
            if (obj["actions"] is JsonArray arr)
            {
                foreach (var item in arr.OfType<JsonObject>())
                {
                    var parsed = ParseToolCall(item);
                    if (parsed != null)
                        list.Add(parsed);
                }
            }

            var single = ParseToolCall(obj["action"]);
            if (single != null && !list.Any(x => string.Equals(x.Name, single.Name, StringComparison.OrdinalIgnoreCase) && string.Equals(x.ArgumentsJson, single.ArgumentsJson, StringComparison.Ordinal)))
                list.Add(single);
        }

        return list;
    }

    private static AiFoundryChatToolCallDto? ParseToolCall(JsonNode? actionNode)
    {
        if (actionNode is not JsonObject obj)
            return null;

        var name = obj["name"]?.ToString()?.Trim();
        if (string.IsNullOrWhiteSpace(name))
            return null;

        return new AiFoundryChatToolCallDto
        {
            Name = name,
            Reason = obj["reason"]?.ToString()?.Trim(),
            ArgumentsJson = obj["arguments"]?.ToJsonString() ?? "{}",
        };
    }

    private static AiFoundryChatExperimentalTraceDto? ParseExperimentalTrace(JsonNode? root)
    {
        if (root is not JsonObject obj || obj["experimental"] is not JsonObject exp)
            return null;

        var trace = new AiFoundryChatExperimentalTraceDto
        {
            Mode = exp["mode"]?.ToString()?.Trim() ?? "standard",
            Summary = exp["summary"]?.ToString()?.Trim(),
            RecommendedKey = exp["recommendedKey"]?.ToString()?.Trim(),
        };

        if (exp["candidates"] is JsonArray arr)
        {
            foreach (var item in arr.OfType<JsonObject>())
            {
                var candidate = new AiFoundryChatExperimentalCandidateDto
                {
                    Key = item["key"]?.ToString()?.Trim() ?? string.Empty,
                    Label = item["label"]?.ToString()?.Trim() ?? string.Empty,
                    PromptStyle = item["promptStyle"]?.ToString()?.Trim(),
                    AssistantMessage = item["assistantMessage"]?.ToString()?.Trim() ?? string.Empty,
                    Score = item["score"]?.GetValue<double?>() ?? 0,
                    ScoreReason = item["scoreReason"]?.ToString()?.Trim(),
                    Recommended = item["recommended"]?.GetValue<bool?>() ?? false,
                };

                if (item["toolCalls"] is JsonArray toolCalls)
                {
                    foreach (var toolNode in toolCalls.OfType<JsonObject>())
                    {
                        var parsedTool = ParseToolCall(toolNode);
                        if (parsedTool != null)
                            candidate.ToolCalls.Add(parsedTool);
                    }
                }

                if (!string.IsNullOrWhiteSpace(candidate.AssistantMessage))
                    trace.Candidates.Add(candidate);
            }
        }

        return trace.Candidates.Count > 0 || !string.IsNullOrWhiteSpace(trace.Summary) ? trace : null;
    }

    private static JsonNode? ParseJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            return JsonNode.Parse(json);
        }
        catch
        {
            return null;
        }
    }

    private static AiFoundryChatToolResultDto FailTool(string summary) => new()
    {
        Status = "failed",
        Summary = summary,
    };

    private static AiFoundryChatToolResultDto NeedsRevisionTool(string summary) => new()
    {
        Status = "needs-revision",
        Summary = summary,
    };

    private static AiFoundryChatToolResultDto ConfirmationRequired(AiFoundryChatToolCallDto toolCall, string summary) => new()
    {
        Status = "confirmation_required",
        Summary = summary,
        RequiresConfirmation = true,
        ConfirmationToolCall = new AiFoundryChatToolCallDto
        {
            Name = toolCall.Name,
            Reason = toolCall.Reason,
            ArgumentsJson = toolCall.ArgumentsJson,
        },
        SuggestedConfirmationMessage = BuildConfirmationRequestMessage(toolCall.Name),
    };

    private static string BuildConfirmationRequestMessage(string? toolName)
        => string.IsNullOrWhiteSpace(toolName)
            ? "Подтверждаю выполнение действия."
            : $"Подтверждаю выполнение действия {toolName.Trim()}.";

    private static string SetConfirmedInArguments(string? argumentsJson)
    {
        JsonObject args;
        try
        {
            args = JsonNode.Parse(argumentsJson ?? "{}") as JsonObject ?? new JsonObject();
        }
        catch
        {
            args = new JsonObject();
        }

        args["confirmed"] = true;
        return args.ToJsonString();
    }

    private static (string? AssistantText, IReadOnlyList<AiFoundryChatToolCallDto> ToolCalls) InterpretChatTurn(
        JsonNode? root,
        AiFoundryChatSession session,
        List<AiFoundryChatMessageDto> messages)
    {
        var assistantText = root?["assistantMessage"]?.ToString()?.Trim();
        var toolCalls = NormalizeChatToolCallsForCurrentState(session, messages, assistantText, ParseToolCalls(root).ToList()).ToList();
        if (!string.IsNullOrWhiteSpace(assistantText) || toolCalls.Count > 0)
        {
            if (string.IsNullOrWhiteSpace(assistantText) && toolCalls.Count > 0)
                assistantText = BuildActionIntro(toolCalls);
            return (assistantText, toolCalls);
        }

        if (root is JsonObject obj)
        {
            var synthesized = TrySynthesizeLegacyAction(obj, session, messages);
            if (synthesized.ToolCall != null)
                return (synthesized.AssistantText, new List<AiFoundryChatToolCallDto> { synthesized.ToolCall });

            var fallbackText = BuildLegacyPlanMessage(obj, session, messages);
            if (!string.IsNullOrWhiteSpace(fallbackText))
                return (fallbackText, Array.Empty<AiFoundryChatToolCallDto>());
        }

        return (assistantText, toolCalls);
    }

    private static IReadOnlyList<AiFoundryChatToolCallDto> NormalizeChatToolCallsForCurrentState(
        AiFoundryChatSession session,
        List<AiFoundryChatMessageDto> messages,
        string? assistantText,
        List<AiFoundryChatToolCallDto> toolCalls)
    {
        if (toolCalls == null || toolCalls.Count == 0)
            return Array.Empty<AiFoundryChatToolCallDto>();

        var memory = DeserializeMemory(session.PlanJson);
        var latestGoal = messages.LastOrDefault(x => string.Equals(x.Role, "user", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(x.Content))?.Content;
        var latestIntentKind = memory.LatestIntentKind ?? memory.AgentState?.LatestIntentKind ?? DetectLatestIntentKind(latestGoal);
        var directorOverride = ApplyDirectorToolCallOverride(session, messages, assistantText, toolCalls, memory, latestGoal, latestIntentKind);
        var normalizedToolCalls = directorOverride?.ToList() ?? toolCalls;
        var preferDirectGeneration = memory.SuppressBridgePlanLoop || memory.AgentState?.PreferDirectGeneration == true || string.Equals(latestIntentKind, "generate", StringComparison.OrdinalIgnoreCase);
        var planOnlyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "prepare_bridge_plan", "show_bridge_plan", "revise_bridge_plan" };
        var allPlanOnly = normalizedToolCalls.All(x => !string.IsNullOrWhiteSpace(x.Name) && planOnlyNames.Contains(x.Name.Trim()));
        if (!preferDirectGeneration || !allPlanOnly)
            return normalizedToolCalls;

        var courseId = session.CourseId
            ?? toolCalls.Select(x => ReadGuid(ParseArgumentsObject(x.ArgumentsJson), "courseId")).FirstOrDefault(x => x.HasValue);
        if (!courseId.HasValue)
            return normalizedToolCalls;

        if (memory.LastBridgePlan != null && memory.LastBridgePlan.CourseId == courseId.Value && memory.LastBridgePlan.Items.Count > 0)
        {
            var selectedIndexes = memory.LastBridgePlan.Items
                .Where(x => !x.Rejected)
                .OrderBy(x => x.Index)
                .Select(x => x.Index)
                .ToArray();
            var args = new JsonObject
            {
                ["courseId"] = courseId.Value,
                ["focus"] = ShortenSingleLine(latestGoal ?? assistantText ?? string.Empty, 240),
                ["prompt"] = memory.LatestTeachingScript ?? memory.LatestExplicitInstruction ?? latestGoal ?? assistantText ?? string.Empty,
            };
            if (selectedIndexes.Length > 0)
                args["itemIndexes"] = new JsonArray(selectedIndexes.Select(x => (JsonNode?)x).ToArray());
            return new List<AiFoundryChatToolCallDto>
            {
                new()
                {
                    Name = "queue_generate_from_text",
                    Reason = "Новый явный запрос пользователя важнее старого plan-loop: переходим сразу к прямой генерации по уже собранному плану без batch.",
                    ArgumentsJson = args.ToJsonString(),
                },
            };
        }

        return new List<AiFoundryChatToolCallDto>
        {
            new()
            {
                Name = "advance_agent_stage",
                Reason = "Пользователь просит не обсуждать план дальше, а довести текущий pipeline до генерации автоматически.",
                ArgumentsJson = JsonSerializer.Serialize(new { courseId = courseId.Value, focus = latestGoal ?? assistantText }, JsonOptions),
            },
        };
    }

    private static (AiFoundryChatToolCallDto? ToolCall, string? AssistantText) TrySynthesizeLegacyAction(
        JsonObject root,
        AiFoundryChatSession session,
        List<AiFoundryChatMessageDto> messages)
    {
        var prompt = ReadString(root, "prompt");
        if (string.IsNullOrWhiteSpace(prompt))
            return (null, null);

        var requestedCount = TryExtractRequestedCount(messages);
        var modelCount = Math.Clamp(ReadInt(root, "count") ?? 5, 1, 50);
        var finalCount = requestedCount ?? modelCount;
        var courseId = ReadGuid(root, "courseId") ?? session.CourseId;
        var assignmentType = ReadString(root, "assignmentType") ?? "code-test";
        var difficulty = Math.Clamp(ReadInt(root, "difficulty") ?? 2, 1, 5);
        var mode = ReadString(root, "mode") ?? "topic-pack";

        var lastUser = messages.LastOrDefault(x => string.Equals(x.Role, "user", StringComparison.OrdinalIgnoreCase))?.Content?.Trim() ?? string.Empty;
        var asksForSeveral = modelCount > 1
            || Regex.IsMatch(lastUser, @"\b(batch|пакет|нескольк|много|ещ[её]|задач)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        if (!courseId.HasValue)
        {
            return (null, "Я собрала черновик генерации, но курс для этого чата не выбран. Сначала выбери курс, и затем я продолжу без повторного объяснения контекста.");
        }

        if (asksForSeveral && requestedCount == null)
        {
            return (null, BuildClarifyCountMessage(root, session));
        }

        var args = new JsonObject
        {
            ["courseId"] = courseId.Value,
            ["prompt"] = prompt,
            ["assignmentType"] = assignmentType,
            ["count"] = finalCount,
            ["difficulty"] = difficulty,
            ["mode"] = mode,
        };

        var notes = ReadString(root, "notes");
        if (!string.IsNullOrWhiteSpace(notes))
            args["notes"] = notes;

        return (
            new AiFoundryChatToolCallDto
            {
                Name = "queue_generate_from_text",
                Reason = "Синтезировано на backend: worker вернул план прямой серии без actions, поэтому чат восстановил ожидаемое действие без batch.",
                ArgumentsJson = args.ToJsonString(),
            },
            $"Поняла. Запускаю прямую серию из {finalCount} задач по текущему контексту курса без batch.");
    }

    private static string? BuildLegacyPlanMessage(JsonObject root, AiFoundryChatSession session, List<AiFoundryChatMessageDto> messages)
    {
        var prompt = ReadString(root, "prompt");
        if (string.IsNullOrWhiteSpace(prompt))
            return ReadString(root, "summary");

        var assignmentType = ReadString(root, "assignmentType") ?? "code-test";
        var difficulty = Math.Clamp(ReadInt(root, "difficulty") ?? 2, 1, 5);
        var mode = ReadString(root, "mode") ?? "topic-pack";
        var count = ReadInt(root, "count") ?? 5;
        var requestedCount = TryExtractRequestedCount(messages);
        var countLabel = requestedCount.HasValue ? requestedCount.Value.ToString() : "не указано";

        return string.Join("\n", new[]
        {
            "Я подготовила черновик генерации по твоему запросу.",
            $"• тип: {assignmentType}",
            $"• сложность: {difficulty}/5",
            $"• режим: {mode}",
            $"• количество из запроса: {countLabel}",
            requestedCount.HasValue ? $"• рабочий count: {requestedCount.Value}" : $"• модель предложила count={count}, но я не запускаю batch без явного количества",
            string.Empty,
            "Черновик prompt:",
            ShortenMultiline(prompt, 700),
        }.Where(x => !string.IsNullOrWhiteSpace(x)));
    }

    private static string BuildClarifyCountMessage(JsonObject root, AiFoundryChatSession session)
    {
        var assignmentType = ReadString(root, "assignmentType") ?? "code-test";
        var difficulty = Math.Clamp(ReadInt(root, "difficulty") ?? 2, 1, 5);
        var mode = ReadString(root, "mode") ?? "topic-pack";
        return string.Join("\n", new[]
        {
            "Я поняла направление и уже собрала черновик batch-плана.",
            "Но количество заданий ты явно не указал, поэтому я не запускаю генерацию молча.",
            "Напиши одним сообщением, сколько задач нужно: 3, 5, 7, 10 или другое число.",
            string.Empty,
            $"Пока вижу так: тип={assignmentType}, сложность={difficulty}/5, режим={mode}."
        });
    }

    private async Task<AiFoundryChatToolResultDto> EnqueueDirectTextGenerationAsync(
        AiFoundryChatSession session,
        List<AiFoundryChatMessageDto> messages,
        Guid courseId,
        string assignmentType,
        string prompt,
        string? sourceText,
        string? titleHint,
        int difficulty,
        int requestedCount,
        string? notes,
        string? structuredContextJson,
        int priority,
        bool enableSelfCheck,
        int instructionStrictness,
        string? userInstructionSnapshot,
        string? teachingScript,
        Guid? createdByUserId,
        string? createdByDisplayName,
        CancellationToken ct,
        AiFoundryChatDraftProposalDto? selectedProposal,
        AiFoundryChatDraftBlueprintDto? blueprint,
        string successSummarySingle,
        string successSummaryMultiTemplate)
    {
        if (requestedCount <= 1)
        {
            var job = await _jobs.QueueGenerateAssignmentFromTextAsync(new AiGenerateAssignmentFromTextRequestDto
            {
                CourseId = courseId,
                AssignmentType = assignmentType,
                Prompt = prompt,
                SourceText = sourceText,
                TitleHint = titleHint,
                Difficulty = difficulty,
                Count = 1,
                Notes = notes,
                StructuredContextJson = structuredContextJson,
                Priority = priority,
                EnableSelfCheck = enableSelfCheck,
                InstructionStrictness = instructionStrictness,
                UserInstructionSnapshot = userInstructionSnapshot,
                TeachingScript = teachingScript,
                ChatSessionId = session.Id,
            }, createdByUserId, createdByDisplayName, ct);

            MarkQueuedBlueprintProposal(session, selectedProposal, blueprint);

            return new AiFoundryChatToolResultDto
            {
                Status = "done",
                Summary = successSummarySingle,
                NavigateTo = "/admin/ai",
                JobId = job.Id,
                CourseId = courseId,
            };
        }

        var slots = AiChatSeriesSlotPlanner.Build(prompt, sourceText, titleHint, requestedCount, difficulty);
        var createdJobs = new List<AiJobDetailsDto>();
        foreach (var slot in slots)
        {
            var slotNotes = string.IsNullOrWhiteSpace(notes)
                ? $"Direct series generation slot {slot.Index}/{slot.TotalCount} from chat session {session.Id}."
                : $"{notes} [slot {slot.Index}/{slot.TotalCount}]";

            var job = await _jobs.QueueGenerateAssignmentFromTextAsync(new AiGenerateAssignmentFromTextRequestDto
            {
                CourseId = courseId,
                AssignmentType = assignmentType,
                Prompt = slot.Prompt,
                SourceText = slot.SourceText,
                TitleHint = slot.TitleHint,
                Difficulty = slot.Difficulty,
                Count = 1,
                Notes = slotNotes,
                StructuredContextJson = structuredContextJson,
                Priority = priority,
                EnableSelfCheck = enableSelfCheck,
                InstructionStrictness = instructionStrictness,
                UserInstructionSnapshot = userInstructionSnapshot,
                TeachingScript = teachingScript,
                ChatSessionId = session.Id,
            }, createdByUserId, createdByDisplayName, ct);
            createdJobs.Add(job);
        }

        MarkQueuedBlueprintProposal(session, selectedProposal, blueprint);

        return new AiFoundryChatToolResultDto
        {
            Status = "done",
            Summary = string.Format(successSummaryMultiTemplate, createdJobs.Count),
            NavigateTo = "/admin/ai",
            JobId = createdJobs.FirstOrDefault()?.Id,
            CourseId = courseId,
        };
    }

    private void MarkQueuedBlueprintProposal(AiFoundryChatSession session, AiFoundryChatDraftProposalDto? selectedProposal, AiFoundryChatDraftBlueprintDto? blueprint)
    {
        if (selectedProposal == null || blueprint == null)
            return;

        selectedProposal.Status = "queued";
        blueprint.ApprovedForDraft = true;
        blueprint.UpdatedAtUtc = DateTime.UtcNow;
        var memory = DeserializeMemory(session.PlanJson);
        session.PlanJson = SerializeMemory(WithCurrentDraftBlueprint(memory, blueprint));
    }

    private static int? TryExtractRequestedCount(List<AiFoundryChatMessageDto> messages)
    {
        foreach (var content in messages
                     .Where(x => string.Equals(x.Role, "user", StringComparison.OrdinalIgnoreCase))
                     .Select(x => x.Content)
                     .Reverse())
        {
            var parsed = TryExtractRequestedCount(content);
            if (parsed.HasValue)
                return parsed;
        }

        return null;
    }

    private static int? TryExtractRequestedCount(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return null;

        var match = Regex.Match(content, @"(?<!\d)(\d{1,2})\s*(?:задач[а-я]*|шт\.?|штук|items?)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success)
            match = Regex.Match(content, @"\bна\s+(\d{1,2})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success)
            return null;

        return int.TryParse(match.Groups[1].Value, out var value) ? Math.Clamp(value, 1, 50) : null;
    }

    private static string BuildActionIntro(IReadOnlyList<AiFoundryChatToolCallDto> toolCalls)
    {
        var names = toolCalls.Select(x => x.Name?.Trim()).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
        if (names.Count == 0)
            return "Запрос обработан. Показываю, что удалось сделать.";
        if (names.Count == 1)
        {
            return (names[0] ?? string.Empty).ToLowerInvariant() switch
            {
                "queue_generate_batch" => "Запускаю прямую серию генерации по текущему контексту.",
                "analyze_course_progression" => "Открываю курс и собираю аудит по пробелам и резким вводам новых тем.",
                "inspect_course_assignments" => "Открываю конкретные задания курса, чтобы сверить стиль и последовательность.",
                "prepare_bridge_plan" => "Собираю подробный план вставок и точек afterAssignmentId по курсу.",
                "show_bridge_plan" => "Показываю текущий план мостиков без пересборки.",
                "revise_bridge_plan" => "Точечно правлю уже собранный план мостиков по твоим замечаниям.",
                "advance_agent_stage" => "Продолжаю агента по памяти и выбираю следующий логичный шаг без повторного объяснения контекста.",
                "queue_generate_bridge_batch" => "Запускаю прямую генерацию мостиков по последнему плану.",
                "save_chat_blueprint" => "Собираю примерные условия прямо в чате.",
            "revise_chat_blueprint" => "Обновляю уже сохранённые примерные условия по новым замечаниям.",
            "show_chat_blueprint" => "Показываю текущие примерные условия из памяти чата.",
            "finalize_chat_blueprint" => "Превращаю согласованные условия из чата в draft-черновики.",
            "drop_chat_blueprint" => "Сбрасываю текущие примерные условия из памяти чата.",
            "queue_generate_from_text" => "Запускаю генерацию из текста.",
            "revise_draft_from_chat" => "Правлю уже готовый AI-черновик по твоим замечаниям.",
                "queue_generate_from_file" => "Запускаю генерацию из файла.",
                "queue_validate_draft" => "Запускаю self-check черновика.",
                "queue_analyze_assignment" => "Запускаю анализ задания.",
                "queue_review_submission" => "Запускаю AI-review попытки.",
                "queue_review_user" => "Запускаю AI-review пользователя.",
                _ => "Выполняю действие по твоему запросу.",
            };
        }

        return $"Поняла. За этот ход выполняю {names.Count} действия.";
    }

    private static bool IsAgentLoopActionName(string? name)
        => !string.IsNullOrWhiteSpace(name) && AgentLoopActionNames.Contains(name.Trim());

    private static JsonObject ParseArgumentsObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new JsonObject();
        try
        {
            return JsonNode.Parse(json) as JsonObject ?? new JsonObject();
        }
        catch
        {
            return new JsonObject();
        }
    }

    private static bool ShouldAutoContinueAgent(string actionMode, IReadOnlyList<AiFoundryChatToolCallDto> toolCalls, IReadOnlyList<AiFoundryChatToolResultDto> toolResults)
    {
        if (!string.Equals(actionMode, "multi", StringComparison.OrdinalIgnoreCase))
            return false;
        if (toolCalls == null || toolCalls.Count == 0)
            return false;
        if (toolResults.Any(x => x.RequiresConfirmation || string.Equals(x.Status, "failed", StringComparison.OrdinalIgnoreCase) || string.Equals(x.Status, "error", StringComparison.OrdinalIgnoreCase)))
            return false;
        if (toolCalls.Count != 1)
            return false;

        var firstName = (toolCalls[0].Name ?? string.Empty).Trim();
        return string.Equals(firstName, "advance_agent_stage", StringComparison.OrdinalIgnoreCase)
            || AutonomousGroundworkActionNames.Contains(firstName);
    }

    private bool ShouldContinueInternalReasoningPass(
        string actionMode,
        AiFoundryChatSession session,
        List<AiFoundryChatMessageDto> messages,
        AiFoundryChatMemoryDto memory,
        IReadOnlyList<AiFoundryChatToolCallDto> accumulatedToolCalls,
        IReadOnlyList<AiFoundryChatToolResultDto> accumulatedToolResults,
        IReadOnlyList<AiFoundryChatToolCallDto> lastToolCalls)
    {
        if (!string.Equals(actionMode, "multi", StringComparison.OrdinalIgnoreCase))
            return false;
        if (lastToolCalls == null || lastToolCalls.Count == 0)
            return false;
        if (accumulatedToolCalls.Count >= 12)
            return false;
        var hasHardFailure = accumulatedToolResults.Any(x => x.RequiresConfirmation || ((string.Equals(x.Status, "failed", StringComparison.OrdinalIgnoreCase) || string.Equals(x.Status, "error", StringComparison.OrdinalIgnoreCase)) && !IsRecoverableAgentToolFailure(x)));
        if (hasHardFailure)
            return false;

        if (accumulatedToolResults.Any(x => string.Equals(x.Status, "needs-revision", StringComparison.OrdinalIgnoreCase) || IsRecoverableAgentToolFailure(x)))
        {
            var revisionLoopTriggered = HasRepeatedBlueprintRevisionLoop(accumulatedToolCalls, accumulatedToolResults);
            if (revisionLoopTriggered)
                _log.LogWarning("[AiChatLoopGuard] session={SessionId} actionMode={ActionMode} objective={ObjectiveKind} reason=blueprint-revision-loop toolCalls={ToolCallCount} toolResults={ToolResultCount}",
                    session.Id,
                    actionMode,
                    memory.AgentState?.ObjectiveKind,
                    accumulatedToolCalls.Count,
                    accumulatedToolResults.Count);
            return !revisionLoopTriggered;
        }

        var latestToolCall = lastToolCalls.LastOrDefault(x => x != null && !string.IsNullOrWhiteSpace(x.Name));
        if (latestToolCall == null)
            return false;

        var latestToolName = latestToolCall.Name.Trim();
        if (AutonomousTerminalActionNames.Contains(latestToolName))
            return false;

        var latestIntent = (memory.LatestIntentKind ?? string.Empty).Trim();
        var objectiveKind = (memory.AgentState?.ObjectiveKind ?? string.Empty).Trim();
        var blueprintIntent = objectiveKind.Equals("chat-blueprint", StringComparison.OrdinalIgnoreCase)
            || latestIntent.Equals("generate", StringComparison.OrdinalIgnoreCase)
            || latestIntent.Equals("show-blueprint", StringComparison.OrdinalIgnoreCase)
            || latestIntent.Equals("revise-blueprint", StringComparison.OrdinalIgnoreCase)
            || latestIntent.Equals("finalize-blueprint", StringComparison.OrdinalIgnoreCase);
        var hasBlueprint = memory.CurrentDraftBlueprint != null && memory.CurrentDraftBlueprint.Proposals.Count > 0;
        var autonomousRework = IsAutonomousReworkIntent(memory.LatestExplicitInstruction);
        var hardDirectorMode = memory.PreferAutonomousCompletion || autonomousRework || IsDirectorWorkflowIntent(memory.LatestExplicitInstruction);

        if (blueprintIntent && !hasBlueprint && lastToolCalls.Any(x => x != null && AutonomousGroundworkActionNames.Contains((x.Name ?? string.Empty).Trim())))
        {
            if (hardDirectorMode)
                return true;
        }

        var latestArgs = ParseArgumentsObject(latestToolCall.ArgumentsJson);
        var courseId = ReadGuid(latestArgs, "courseId") ?? session.CourseId;
        if (!courseId.HasValue)
            return false;

        var nextToolCall = BuildNextAgentToolCall(courseId.Value, session, messages, latestArgs);
        if (nextToolCall == null || string.IsNullOrWhiteSpace(nextToolCall.Name))
            return false;

        var nextSignature = BuildToolCallLoopSignature(nextToolCall);
        var alreadyVisited = accumulatedToolCalls.Any(x => string.Equals(BuildToolCallLoopSignature(x), nextSignature, StringComparison.OrdinalIgnoreCase));
        return !alreadyVisited;
    }

    private static bool HasRepeatedBlueprintRevisionLoop(
        IReadOnlyList<AiFoundryChatToolCallDto> accumulatedToolCalls,
        IReadOnlyList<AiFoundryChatToolResultDto> recentToolResults)
    {
        if (accumulatedToolCalls == null || accumulatedToolCalls.Count < 2 || recentToolResults == null || recentToolResults.Count < 2)
            return false;

        var recentNames = accumulatedToolCalls
            .TakeLast(6)
            .Select(x => (x.Name ?? string.Empty).Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();
        var blueprintNames = recentNames
            .Where(x => string.Equals(x, "save_chat_blueprint", StringComparison.OrdinalIgnoreCase) || string.Equals(x, "revise_chat_blueprint", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (blueprintNames.Count < 2)
            return false;
        if (recentNames.Any(x => !string.Equals(x, "save_chat_blueprint", StringComparison.OrdinalIgnoreCase) && !string.Equals(x, "revise_chat_blueprint", StringComparison.OrdinalIgnoreCase)))
            return false;

        var recentRevisionFingerprints = recentToolResults
            .Where(x => string.Equals(x.Status, "needs-revision", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(x.Summary))
            .Select(BuildRevisionFingerprint)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .TakeLast(4)
            .ToList();
        if (recentRevisionFingerprints.Count < 2)
            return false;

        if (recentRevisionFingerprints.Count >= 3)
            return true;

        return string.Equals(recentRevisionFingerprints[^1], recentRevisionFingerprints[^2], StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRecoverableAgentToolFailure(AiFoundryChatToolResultDto? result)
    {
        if (result == null)
            return false;

        var status = (result.Status ?? string.Empty).Trim();
        if (string.Equals(status, "needs-revision", StringComparison.OrdinalIgnoreCase))
            return true;
        if (!string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase) && !string.Equals(status, "error", StringComparison.OrdinalIgnoreCase))
            return false;

        var summary = (result.Summary ?? string.Empty).Trim();
        return summary.Contains("Сначала вызови", StringComparison.OrdinalIgnoreCase)
            || summary.Contains("Нужно выбрать courseId", StringComparison.OrdinalIgnoreCase)
            || summary.Contains("Blueprint пока не удовлетворяет", StringComparison.OrdinalIgnoreCase)
            || summary.Contains("requires fresh course audit", StringComparison.OrdinalIgnoreCase)
            || summary.Contains("requires courseId", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeActionMode(string? actionMode)
    {
        var normalized = (actionMode ?? string.Empty).Trim().ToLowerInvariant();
        if (normalized is "experimental" or "lab" or "variants" or "multi-variant")
            return "experimental";
        return normalized is "single" or "mono" or "step" or "manual" ? "single" : "multi";
    }

    private static string BuildContinuationProcessingMessage(
        IReadOnlyList<AiFoundryChatToolCallDto> toolCalls,
        IReadOnlyList<AiFoundryChatToolResultDto> toolResults,
        AiFoundryChatMemoryDto memory)
    {
        var flow = toolCalls
            .Select(x => DescribeToolName(x.Name))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(4)
            .ToList();
        var validationSummary = toolResults
            .Where(x => string.Equals(x.Status, "needs-revision", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(x.Summary))
            .Select(x => ShortenSingleLine(x.Summary, 180))
            .LastOrDefault();

        if (!string.IsNullOrWhiteSpace(validationSummary))
            return "Я поправила формат и добираю черновики до нужной формы.";
        if (flow.Count > 0)
            return $"Продолжаю внутреннюю проверку: уже выполнила {string.Join(" → ", flow)} и сейчас добираю недостающий шаг, чтобы не отвечать вслепую.";
        return !string.IsNullOrWhiteSpace(memory.AgentState?.ObjectiveSummary)
            ? $"Продолжаю внутреннюю проверку под цель: {ShortenSingleLine(memory.AgentState.ObjectiveSummary, 160)}"
            : "Продолжаю внутреннюю проверку и собираю недостающий контекст перед финальным ответом.";
    }


    private static string BuildLoopGuardAssistantMessage(
        IReadOnlyList<AiFoundryChatToolResultDto> toolResults,
        AiFoundryChatMemoryDto memory)
    {
        var latestRevision = toolResults
            .Where(x => string.Equals(x.Status, "needs-revision", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(x.Summary))
            .Select(x => ShortenSingleLine(x.Summary, 220))
            .LastOrDefault();
        var objective = ShortenSingleLine(memory.AgentState?.ObjectiveSummary ?? memory.LatestExplicitInstruction ?? string.Empty, 180);
        if (!string.IsNullOrWhiteSpace(latestRevision))
            return "Я не стала показывать внутреннюю ошибку проверки. Черновики нужно пересобрать в более точной форме: сохранить нужное количество и дружелюбный пошаговый формат.";
        return !string.IsNullOrWhiteSpace(objective)
            ? $"Остановила авто-цикл по цели «{objective}». Нужна явная корректировка от пользователя, иначе агент будет повторять одни и те же шаги." 
            : "Остановила авто-цикл: дальнейшее автопродолжение дублирует предыдущие шаги и не даёт нового результата.";
    }

    private static bool ShouldStopAutoAgentLoop(string? actionName, AiFoundryChatToolResultDto? result)
    {
        if (result?.RequiresConfirmation == true)
            return true;
        if ((string.Equals(result?.Status, "failed", StringComparison.OrdinalIgnoreCase) || string.Equals(result?.Status, "error", StringComparison.OrdinalIgnoreCase))
            && !IsRecoverableAgentToolFailure(result))
            return true;
        return string.Equals(actionName, "show_bridge_plan", StringComparison.OrdinalIgnoreCase)
            || string.Equals(actionName, "revise_bridge_plan", StringComparison.OrdinalIgnoreCase)
            || string.Equals(actionName, "queue_generate_bridge_batch", StringComparison.OrdinalIgnoreCase)
            || string.Equals(actionName, "queue_generate_batch", StringComparison.OrdinalIgnoreCase)
            || string.Equals(actionName, "queue_generate_from_text", StringComparison.OrdinalIgnoreCase)
            || string.Equals(actionName, "revise_draft_from_chat", StringComparison.OrdinalIgnoreCase)
            || string.Equals(actionName, "queue_generate_from_file", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildToolCallLoopSignature(AiFoundryChatToolCallDto toolCall)
        => $"{(toolCall.Name ?? string.Empty).Trim().ToLowerInvariant()}|{NormalizeLoopArguments(toolCall.ArgumentsJson)}";

    private static string NormalizeLoopArguments(string? json)
        => string.IsNullOrWhiteSpace(json) ? "{}" : Regex.Replace(json.Trim(), @"\s+", string.Empty);

    private static string? BuildAgentLoopSummary(IReadOnlyList<string> requestedNames, IReadOnlyList<string> autoNames, AiFoundryChatToolResultDto? finalResult)
    {
        var flow = requestedNames
            .Concat(autoNames)
            .Select(DescribeToolName)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(6)
            .ToList();
        if (flow.Count == 0)
            return null;

        var resultText = !string.IsNullOrWhiteSpace(finalResult?.Summary)
            ? $" Финальный шаг: {ShortenSingleLine(finalResult!.Summary!, 220)}"
            : string.Empty;
        return $"Агент сам продолжил ход и последовательно выполнил: {string.Join(" → ", flow)}.{resultText}";
    }
    private static string BuildAssistantContent(string? assistantText, IReadOnlyList<AiFoundryChatToolResultDto>? toolResults)
    {
        var text = string.IsNullOrWhiteSpace(assistantText)
            ? "AI завершила обработку, но не вернула текстовый комментарий."
            : assistantText.Trim();

        var visibleSummaries = (toolResults ?? Array.Empty<AiFoundryChatToolResultDto>())
            .Where(ShouldAppendToolResultToAssistantText)
            .Select(x => x.Summary?.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (visibleSummaries.Count > 0)
        {
            var appendix = string.Join("\n", visibleSummaries.Select(x => $"• {x}"));
            if (!string.IsNullOrWhiteSpace(appendix) && !text.Contains(appendix, StringComparison.OrdinalIgnoreCase))
                text = $"{text}\n\n{appendix}";
        }

        return text;
    }

    private static bool ShouldAppendToolResultToAssistantText(AiFoundryChatToolResultDto? result)
    {
        if (result == null)
            return false;
        if (result.RequiresConfirmation)
            return true;

        var status = (result.Status ?? string.Empty).Trim();
        if (string.Equals(status, "needs-revision", StringComparison.OrdinalIgnoreCase))
            return false;

        if (string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "error", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "cancelled", StringComparison.OrdinalIgnoreCase))
            return true;

        return string.IsNullOrWhiteSpace(result.Summary) && !string.IsNullOrWhiteSpace(result.NavigateTo);
    }

    private static AiFoundryChatAttachmentDto? ResolveAttachment(List<AiFoundryChatMessageDto> messages, string? preferredFileKey)
    {
        if (!string.IsNullOrWhiteSpace(preferredFileKey))
        {
            var exact = messages
                .Where(x => string.Equals(x.Role, "user", StringComparison.OrdinalIgnoreCase))
                .SelectMany(x => x.Attachments ?? new List<AiFoundryChatAttachmentDto>())
                .LastOrDefault(x => string.Equals(x.FileKey, preferredFileKey, StringComparison.OrdinalIgnoreCase));
            if (exact != null)
                return exact;
        }

        return messages
            .Where(x => string.Equals(x.Role, "user", StringComparison.OrdinalIgnoreCase))
            .SelectMany(x => x.Attachments ?? new List<AiFoundryChatAttachmentDto>())
            .LastOrDefault();
    }

    private static string BuildSourceTextFromRecentAttachments(List<AiFoundryChatMessageDto> messages)
    {
        var chunks = messages
            .Where(x => string.Equals(x.Role, "user", StringComparison.OrdinalIgnoreCase))
            .SelectMany(x => x.Attachments ?? new List<AiFoundryChatAttachmentDto>())
            .Select(x => x.TextExcerpt)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .TakeLast(3)
            .ToList();

        return chunks.Count == 0 ? string.Empty : string.Join("\n\n", chunks);
    }

    private static string BuildFallbackPrompt(List<AiFoundryChatMessageDto> messages)
    {
        var lastUser = messages.LastOrDefault(x => string.Equals(x.Role, "user", StringComparison.OrdinalIgnoreCase));
        return string.IsNullOrWhiteSpace(lastUser?.Content)
            ? "Сгенерируй задания по последнему контексту чата."
            : lastUser!.Content.Trim();
    }

    private static string BuildSessionTitle(List<AiFoundryChatMessageDto> messages)
    {
        var firstUser = messages.FirstOrDefault(x => string.Equals(x.Role, "user", StringComparison.OrdinalIgnoreCase));
        var raw = (firstUser?.Content ?? "Новый AI-чат").Trim();
        return ClampTitle(raw);
    }

    private static string ClampTitle(string raw)
    {
        raw = string.IsNullOrWhiteSpace(raw) ? "Новый AI-чат" : raw.Trim();
        if (raw.Length > 64)
            raw = raw[..61] + "...";
        return raw;
    }

    private static string? BuildStructuredBatchContextJson(
        AiFoundryChatSession session,
        List<AiFoundryChatMessageDto> messages,
        AiFoundryChatMemoryDto memory,
        Guid courseId,
        string prompt,
        JsonObject args,
        string batchKind,
        int requestedCount,
        int difficulty)
    {
        var audit = memory.LastCourseAudit != null && memory.LastCourseAudit.CourseId == courseId ? memory.LastCourseAudit : null;
        var inspection = memory.LastCourseInspection != null && memory.LastCourseInspection.CourseId == courseId ? memory.LastCourseInspection : null;
        var bridgePlan = memory.LastBridgePlan != null && memory.LastBridgePlan.CourseId == courseId ? memory.LastBridgePlan : null;
        var chatText = string.Join("\n", messages
            .Where(x => string.Equals(x.Role, "user", StringComparison.OrdinalIgnoreCase))
            .TakeLast(8)
            .Select(x => x.Content));
        var notes = ReadString(args, "notes");
        var learnerProfile = BuildLearnerProfileSnapshot(chatText, prompt, notes);
        var constraints = BuildGenerationConstraintsSnapshot(chatText, prompt, notes);
        var placementPlan = BuildPlacementPlanSnapshot(audit, bridgePlan, requestedCount);
        var scenarioProfile = AiGenerationScenarioRouter.Resolve(memory, prompt, ReadString(args, "sourceText"), requestedCount);
        var titleExamples = new List<string>();
        if (bridgePlan != null)
            titleExamples.AddRange(bridgePlan.Items.SelectMany(x => x.TitleExamples ?? new List<string>()));
        if (audit != null)
            titleExamples.AddRange(audit.TitleExamples ?? new List<string>());
        if (inspection != null)
            titleExamples.AddRange(inspection.Assignments.Select(x => x.Title));
        titleExamples = titleExamples
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => ShortenSingleLine(x, 90))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToList();

        var styleHints = new List<string>();
        if (audit != null)
            styleHints.AddRange(audit.StyleHints ?? new List<string>());
        if (bridgePlan != null)
            styleHints.AddRange(bridgePlan.StyleHints ?? new List<string>());
        styleHints = styleHints
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => ShortenSingleLine(x, 120))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .ToList();

        var payload = new
        {
            source = "chat-session",
            sessionId = session.Id,
            batchKind,
            requestedCount,
            difficulty,
            requireCourseAwarePlanning = placementPlan.Count > 0 || audit != null,
            userIntentSummary = ShortenSingleLine(prompt, 260),
            latestIntentKind = memory.LatestIntentKind,
            latestExplicitInstruction = ShortenSingleLine(memory.LatestExplicitInstruction, 260),
            latestTeachingScript = memory.LatestTeachingScript,
            suppressBridgePlanLoop = memory.SuppressBridgePlanLoop,
            learnerProfile,
            pedagogy = new
            {
                preferGuidedWalkthroughs = Convert.ToBoolean(learnerProfile["preferGuidedWalkthroughs"]) || scenarioProfile.PreferGuidedWalkthroughs,
                requireSectionIntroGuides = learnerProfile["requireSectionIntroGuides"],
                explainLikeChild = learnerProfile["explainLikeChild"],
                tone = learnerProfile["tone"],
                vocabularyLevel = learnerProfile["vocabularyLevel"],
                maxNewConceptsPerTask = learnerProfile["maxNewConceptsPerTask"],
                requireConcreteExamples = true,
                preferTinySteps = scenarioProfile.PreferTinySteps,
                preferActionVerbs = true,
            },
            scenario = new
            {
                scenarioProfile.Id,
                scenarioProfile.DisplayName,
                scenarioProfile.Family,
                scenarioProfile.DefaultBatchMode,
                scenarioProfile.PreferGuidedWalkthroughs,
                scenarioProfile.PreferTinySteps,
                scenarioProfile.ForceSmallPrograms,
                scenarioProfile.RequireExplicitIf,
                scenarioProfile.PreferSingleDeepTask,
                scenarioProfile.PreferCourseAudit,
                scenarioProfile.DefaultCount,
                scenarioProfile.Score,
                matchedSignals = scenarioProfile.MatchedSignals,
            },
            titleStyle = new
            {
                pattern = titleExamples.Count > 0 ? "Следуй pattern существующих названий курса и избегай общих labels." : "Короткое course-native название.",
                examples = titleExamples,
                styleHints,
                avoidGenericTitles = true,
            },
            chatMemory = new
            {
                summary = ShortenSingleLine(memory.Summary, 220),
                facts = (memory.Facts ?? new List<string>()).Take(6).ToList(),
                recentGoals = (memory.RecentGoals ?? new List<string>()).Take(4).ToList(),
                latestIntentKind = memory.LatestIntentKind,
                latestExplicitInstruction = ShortenSingleLine(memory.LatestExplicitInstruction, 220),
                latestTeachingScript = memory.LatestTeachingScript,
                suppressBridgePlanLoop = memory.SuppressBridgePlanLoop,
                agentState = memory.AgentState,
            },
            agentState = BuildBatchAgentStateSnapshot(memory, courseId, prompt, batchKind, requestedCount, difficulty, learnerProfile, constraints, styleHints, placementPlan),
            courseAudit = audit == null ? null : new
            {
                audit.Summary,
                audit.Focus,
                styleHints = styleHints,
                titleExamples = titleExamples.Take(8).ToList(),
                findings = audit.Findings.Take(8).Select(x => new
                {
                    concept = x.Concept,
                    afterAssignmentId = x.AfterAssignmentId,
                    afterAssignmentTitle = x.AfterAssignmentTitle,
                    beforeAssignmentId = x.BeforeAssignmentId,
                    beforeAssignmentTitle = x.BeforeAssignmentTitle,
                    x.Reason,
                    suggestedTaskCount = x.SuggestedTaskCount,
                    suggestedDifficulty = Math.Clamp(x.SuggestedDifficulty ?? 1, 1, 3),
                }).ToList(),
            },
            courseInspection = inspection == null ? null : new
            {
                inspection.Summary,
                assignments = inspection.Assignments.Take(12).Select(x => new
                {
                    x.Id,
                    x.Sort,
                    x.Difficulty,
                    x.Title,
                    x.DescriptionExcerpt,
                }).ToList(),
            },
            bridgePlan = bridgePlan == null ? null : new
            {
                bridgePlan.Summary,
                bridgePlan.Status,
                items = bridgePlan.Items.Where(x => !x.Rejected).Take(12).Select(x => new
                {
                    x.Index,
                    x.Concept,
                    x.AfterAssignmentId,
                    x.AfterAssignmentTitle,
                    x.BeforeAssignmentId,
                    x.BeforeAssignmentTitle,
                    x.Reason,
                    x.TaskCount,
                    x.Difficulty,
                    x.TitleHint,
                    titleExamples = (x.TitleExamples ?? new List<string>()).Take(6).ToList(),
                    x.Confirmed,
                }).ToList(),
            },
            placementPlan,
            constraints,
        };

        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    private static Dictionary<string, object?> BuildLearnerProfileSnapshot(string chatText, string prompt, string? notes)
    {
        var hay = string.Join(" ", new[] { chatText, prompt, notes ?? string.Empty }).ToLowerInvariant();
        var isYoungBeginners = hay.Contains("первокласс") || hay.Contains("дошколь") || hay.Contains("маленьк") || hay.Contains("дети");
        var isBeginners = isYoungBeginners || hay.Contains("нович") || hay.Contains("с нуля") || hay.Contains("должны всё понять") || hay.Contains("очень просто") || hay.Contains("простым языком");
        var preferGuides = hay.Contains("пошаг") || hay.Contains("путевод") || hay.Contains("пошагово") || hay.Contains("по шагам");
        var sectionIntroGuides = preferGuides || hay.Contains("перед началом раздел") || hay.Contains("в начале раздел") || hay.Contains("перед началом темы") || hay.Contains("в начале темы");
        return new Dictionary<string, object?>
        {
            ["audience"] = isYoungBeginners ? "young-beginners" : (isBeginners ? "beginners" : "general"),
            ["explainLikeChild"] = isYoungBeginners,
            ["preferGuidedWalkthroughs"] = preferGuides || isYoungBeginners,
            ["requireSectionIntroGuides"] = sectionIntroGuides,
            ["tone"] = isYoungBeginners ? "very-simple" : (isBeginners ? "simple" : "standard"),
            ["vocabularyLevel"] = isYoungBeginners ? "kids" : (isBeginners ? "basic" : "standard"),
            ["maxNewConceptsPerTask"] = isYoungBeginners ? 1 : (isBeginners ? 1 : 2),
        };
    }

    private static Dictionary<string, object?> BuildGenerationConstraintsSnapshot(string chatText, string prompt, string? notes)
    {
        var hay = string.Join(" ", new[] { chatText, prompt, notes ?? string.Empty }).ToLowerInvariant();
        var mustStayBefore = new List<string>();
        var avoid = new List<string>();
        void AddIfMentioned(string marker, string value)
        {
            if (hay.Contains(marker) && !mustStayBefore.Contains(value, StringComparer.OrdinalIgnoreCase))
                mustStayBefore.Add(value);
        }
        AddIfMentioned("до цикл", "циклы");
        AddIfMentioned("перед цикл", "циклы");
        AddIfMentioned("до массив", "массивы");
        AddIfMentioned("до функц", "функции");
        AddIfMentioned("перед строк", "строки с пробелами");
        if (hay.Contains("без цикл")) avoid.Add("циклы");
        if (hay.Contains("без массив")) avoid.Add("массивы");
        if (hay.Contains("без функц")) avoid.Add("функции");
        return new Dictionary<string, object?>
        {
            ["mustStayBeforeConcepts"] = mustStayBefore,
            ["avoidConcepts"] = avoid.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            ["styleGoal"] = "course-native",
            ["titleGoal"] = "короткие конкретные названия как в курсе",
        };
    }

    private static string DetectLatestIntentKind(string? text)
    {
        var low = (text ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(low))
            return "chat";
        if (IsAnchorLocationLookupIntent(low))
            return "inspect";
        if (IsCourseGapRemediationIntent(low))
            return "remediation";
        if (IsDiagnosticGapAuditIntent(low))
            return "audit";
        if (AiGenerationScenarioPolicy.LooksLikeScenarioGenerationIntent(low))
            return "generate";
        if (IsDirectorWorkflowIntent(low) || IsAutonomousReworkIntent(low) || IsDirectFinalResultIntent(low))
            return "generate";
        if (IsInspectCourseIntent(low))
            return "inspect";
        if (low.Contains("покажи план") || low.Contains("какой план") || low.Contains("что в плане") || low.Contains("show_bridge_plan"))
            return "show-plan";
        if ((low.Contains("поправь план") || low.Contains("измени план") || low.Contains("исправь план") || low.Contains("поставь её второй") || low.Contains("поставь ее второй") || low.Contains("вторым") || low.Contains("сделай задачку") || low.Contains("добавь вторым"))
            && !low.Contains("не продолжай старый план"))
            return "revise-plan";
        if (low.Contains("одобря") || low.Contains("закидывай в черновик") || low.Contains("в черновик") || low.Contains("финализируй") || low.Contains("сделай черновик"))
            return "finalize-blueprint";
        if (IsChatBlueprintDraftRequest(low))
            return "show-blueprint";
        if (low.Contains("покажи варианты") || low.Contains("какие варианты") || low.Contains("покажи услов") || low.Contains("покажи наброс"))
            return "show-blueprint";
        if ((low.Contains("поправ") || low.Contains("исправ") || low.Contains("измени") || low.Contains("доработ") || low.Contains("перепиш"))
            && (low.Contains("вариант") || low.Contains("услов") || low.Contains("тест") || low.Contains("задач")))
            return "revise-blueprint";
        if (low.Contains("начни заново") || low.Contains("сбрось варианты") || low.Contains("удали варианты") || low.Contains("очисти условия"))
            return "drop-blueprint";
        if (low.Contains("не план") || low.Contains("не revise") || low.Contains("не show") || low.Contains("саму задачу") || low.Contains("готовую задачу") || low.Contains("готовый текст") || low.Contains("создай черновик") || low.Contains("создай draft") || low.Contains("сразу генерац") || low.Contains("сгенерируй") || low.Contains("создай зада") || low.Contains("сделай зада") || Regex.IsMatch(low, @"\bвсе[,! ]*делай\b|\bвсё[,! ]*делай\b|\bделай\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            return "generate";
        if ((low.Contains("план") || low.Contains("мостик") || low.Contains("подводящ")) && !low.Contains("не продолжай старый план"))
            return "plan";
        return "chat";
    }

    private static bool IsChatBlueprintDraftRequest(string? text)
    {
        var low = (text ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(low))
            return false;
        var hasDraftMarker = low.Contains("черновик") || low.Contains("черновики") || low.Contains("вариант") || low.Contains("варианты") || low.Contains("наброс") || low.Contains("условия") || low.Contains("условие");
        var hasAction = low.Contains("напиши") || low.Contains("покажи") || low.Contains("собери") || low.Contains("подготов") || low.Contains("накидай") || low.Contains("набросай") || low.Contains("придумай");
        var hasTarget = low.Contains("к этим задач") || low.Contains("для этих задач") || low.Contains("по этим задач") || low.Contains("лесенк") || low.Contains("пошаг") || low.Contains("маленьких программ") || low.Contains("if") || low.Contains("ветвл") || low.Contains("условн");
        if (!(hasDraftMarker || hasAction) || !hasTarget)
            return false;
        if (low.Contains("готовый draft") || low.Contains("опубликован") || low.Contains("публикац"))
            return false;
        return true;
    }

    private static bool IsDirectorWorkflowIntent(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var hay = text.ToLowerInvariant();
        var mentionsDirector = hay.Contains("дириж") || hay.Contains("не как обычный текстоген") || hay.Contains("execution contract");
        var mentionsOrderedFlow = hay.Contains("обязательный порядок действий")
            || (hay.Contains("сначала") && hay.Contains("затем") && hay.Contains("после этого"));
        var mentionsGenerationGoal = hay.Contains("сгенер") || hay.Contains("придум") || hay.Contains("новых задач") || hay.Contains("обучающих задач");
        return mentionsDirector || (mentionsOrderedFlow && mentionsGenerationGoal);
    }

    private static bool IsAnchorLocationLookupIntent(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var hay = text.ToLowerInvariant();
        var concept = DetectAnchorConceptFromText(hay);
        if (string.IsNullOrWhiteSpace(concept))
            return false;

        var asksWhere = hay.Contains("где появ")
            || hay.Contains("где ввод")
            || hay.Contains("где встреч")
            || hay.Contains("найди именно")
            || hay.Contains("первое задание")
            || hay.Contains("первое появление")
            || hay.Contains("где в курсе")
            || hay.Contains("в каком задан")
            || hay.Contains("посмотри где")
            || hay.Contains("изучи где");
        var wantsPreview = hay.Contains("перед этим")
            || hay.Contains("до этого")
            || hay.Contains("перед тем")
            || hay.Contains("до темы")
            || hay.Contains("обучал")
            || hay.Contains("научат")
            || hay.Contains("пользоваться")
            || hay.Contains("работать с");
        return asksWhere || (wantsPreview && QueryMentionsFirstAnchor(hay, concept));
    }

    private static bool IsCourseGapRemediationIntent(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var hay = text.ToLowerInvariant();
        var asksForCoverage = new[]
        {
            "все пробел", "все дыр", "все косяк", "все слабые места", "закрой пробел", "закрыть пробел",
            "на все пробел", "по всем пробел", "весь курс", "по всему курсу", "реально проанализируй",
            "предложи решения", "предложи мостики", "предложи как закрыть", "найди пробелы и",
            "сгенерируй задачи на все", "собери задачи на все", "исправь пробелы", "ремеди"
        }.Any(x => hay.Contains(x, StringComparison.Ordinal));
        var mentionsCourse = new[] { "курс", "курса", "курсе", "обучал", "лесенк", "педагог", "мостик", "подводящ" }
            .Any(x => hay.Contains(x, StringComparison.Ordinal));
        return asksForCoverage && mentionsCourse;
    }

    private static bool IsInspectCourseIntent(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var hay = text.ToLowerInvariant();
        var mentionsAssignments = hay.Contains("задан") || hay.Contains("assignment") || hay.Contains("урок") || hay.Contains("курс");
        var asksToShow = hay.Contains("выведи")
            || hay.Contains("выводи")
            || hay.Contains("покажи")
            || hay.Contains("показывай")
            || hay.Contains("список")
            || hay.Contains("перечисли")
            || hay.Contains("какие")
            || hay.Contains("изучи задачи курса")
            || hay.Contains("где появ")
            || hay.Contains("где ввод")
            || hay.Contains("найди именно")
            || hay.Contains("в каком задан");
        var avoidsPlanning = !hay.Contains("мостик") && !hay.Contains("подводящ") && !hay.Contains("план");
        var asksToGenerate = AiGenerationScenarioPolicy.LooksLikeScenarioGenerationIntent(hay)
            || hay.Contains("сгенер")
            || hay.Contains("придум")
            || hay.Contains("создай")
            || hay.Contains("сделай сери")
            || hay.Contains("новых задач")
            || hay.Contains("встав")
            || hay.Contains("перед первым if")
            || hay.Contains("перед первым появлением if")
            || hay.Contains("обязательный порядок действий")
            || hay.Contains("готовый результат");
        return mentionsAssignments && asksToShow && avoidsPlanning && !asksToGenerate && !IsDirectorWorkflowIntent(hay);
    }

    private static string? ExtractLatestTeachingScript(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var raw = text.Trim();
        var low = raw.ToLowerInvariant();
        var looksLikeScript = low.Contains("#include")
            || low.Contains("int main")
            || low.Contains("cout <<")
            || low.Contains("cin >>")
            || low.Contains("эталонное решение")
            || low.Contains("должно быть расписано")
            || low.Contains("по одной строчке")
            || low.Contains("что писать и зачем");
        if (!looksLikeScript)
            return null;
        return raw.Length <= 2200 ? raw : raw[..2200];
    }

    private static bool ShouldSuppressBridgePlanLoop(string? latestGoal, string? latestIntentKind, string? latestTeachingScript)
    {
        if (string.Equals(latestIntentKind, "generate", StringComparison.OrdinalIgnoreCase)
            || string.Equals(latestIntentKind, "audit", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrWhiteSpace(latestTeachingScript))
            return true;
        var low = (latestGoal ?? string.Empty).ToLowerInvariant();
        return low.Contains("не возвращайся к план")
            || low.Contains("не показывай план")
            || low.Contains("не делай новый план")
            || low.Contains("не show_bridge_plan")
            || low.Contains("не revise_bridge_plan")
            || low.Contains("нужна сама задача")
            || low.Contains("нужен именно текст задачи")
            || low.Contains("делай всё сразу")
            || low.Contains("делай все сразу")
            || low.Contains("всё, делай")
            || low.Contains("все, делай")
            || low.Contains("покажи только итог")
            || low.Contains("не дорабатывай старые условия")
            || low.Contains("предыдущие черновики недействительны")
            || low.Contains("уже можно принимать в работу");
    }

    private static bool ShouldPreferAutonomousCompletion(string? latestGoal, string? latestIntentKind)
    {
        if (string.IsNullOrWhiteSpace(latestGoal))
            return false;

        if (string.Equals(latestIntentKind, "remediation", StringComparison.OrdinalIgnoreCase))
            return true;
        if (IsDirectorWorkflowIntent(latestGoal))
            return true;
        if (AiGenerationScenarioPolicy.RequestsDirectResultWithoutProgress(latestGoal))
            return true;

        var low = latestGoal.ToLowerInvariant();
        return low.Contains("не один шаг")
            || low.Contains("несколько раз")
            || low.Contains("пока сама не удовлетвор")
            || low.Contains("пока сам не удовлетвор")
            || low.Contains("самостоятельност")
            || low.Contains("не спрашивай")
            || low.Contains("без согласования")
            || low.Contains("без моего дополнительного сообщения")
            || low.Contains("сделай всё за один")
            || low.Contains("сделай все за один")
            || low.Contains("за один мой запрос")
            || low.Contains("плевать если долго")
            || low.Contains("сама добери")
            || low.Contains("сам добери")
            || low.Contains("не останавливайся")
            || low.Contains("пока не выполн")
            || low.Contains("пока не убед")
            || low.Contains("сама не удовлетворишься")
            || low.Contains("сам не удовлетворишься")
            || low.Contains("без промежуточного согласования")
            || low.Contains("без промежуточных остановок")
            || low.Contains("не просить у меня одобрение")
            || low.Contains("не просить у меня подтверждение")
            || low.Contains("не проси у меня одобрение")
            || low.Contains("не проси у меня подтверждение")
            || low.Contains("если ты не уверен — продолжай")
            || low.Contains("если ты не уверен - продолжай")
            || low.Contains("повтори решение заново")
            || low.Contains("раскритикуй свой предыдущий результат")
            || low.Contains("сразу исправь результат полностью")
            || low.Contains("не продолжай старый план")
            || low.Contains("не сохраняй промежуточный мусор")
            || low.Contains("покажи только итог")
            || low.Contains("не дорабатывай старые условия")
            || low.Contains("все предыдущие черновики недействительны")
            || low.Contains("готовый результат")
            || low.Contains("уже можно принимать в работу");
    }

    private static bool IsAutonomousReworkIntent(string? latestGoal)
    {
        if (string.IsNullOrWhiteSpace(latestGoal))
            return false;

        var low = latestGoal.ToLowerInvariant();
        return low.Contains("повтори решение заново")
            || low.Contains("сам раскритикуй")
            || low.Contains("раскритикуй свой предыдущий результат")
            || low.Contains("сразу исправь результат полностью")
            || low.Contains("если найдёшь хотя бы одну проблему")
            || low.Contains("если найдешь хотя бы одну проблему")
            || low.Contains("не останавливайся на промежуточном ответе")
            || low.Contains("без промежуточного согласования")
            || low.Contains("не продолжай старый план")
            || low.Contains("не сохраняй промежуточный мусор")
            || low.Contains("покажи только итог")
            || low.Contains("не дорабатывай старые условия")
            || low.Contains("все предыдущие черновики недействительны")
            || low.Contains("готовый результат")
            || low.Contains("уже можно принимать в работу");
    }

    private static bool IsStartOverFromScratchIntent(string? latestGoal)
    {
        if (string.IsNullOrWhiteSpace(latestGoal))
            return false;

        var low = latestGoal.ToLowerInvariant();
        return low.Contains("с нуля")
            || low.Contains("заново найди")
            || low.Contains("заново открой")
            || low.Contains("заново проверь")
            || low.Contains("все предыдущие черновики недействительны")
            || low.Contains("все предыдущие черновики не действительны")
            || low.Contains("не продолжай старый план")
            || low.Contains("не дорабатывай старые условия");
    }


    private static bool IsDirectFinalResultIntent(string? latestGoal)
    {
        if (string.IsNullOrWhiteSpace(latestGoal))
            return false;

        var low = latestGoal.ToLowerInvariant();
        return low.Contains("покажи только итог")
            || low.Contains("только итог")
            || low.Contains("только готовый результат")
            || low.Contains("готовый результат")
            || low.Contains("готовый итог")
            || low.Contains("готовый ответ")
            || low.Contains("уже можно принимать в работу")
            || low.Contains("уже можно брать в работу")
            || low.Contains("без промежуточного согласования")
            || low.Contains("без промежуточных остановок")
            || low.Contains("без моего дополнительного сообщения")
            || low.Contains("без одобрения")
            || low.Contains("не проси у меня одобрение")
            || low.Contains("не проси у меня подтверждение")
            || low.Contains("не просить у меня одобрение")
            || low.Contains("не просить у меня подтверждение")
            || low.Contains("сделай всю работу за одно")
            || low.Contains("за одно моё сообщение")
            || low.Contains("за один мой запрос")
            || low.Contains("покажи только итог, который уже можно принимать")
            || low.Contains("не дорабатывай старые условия")
            || low.Contains("все предыдущие черновики недействительны");
    }

    private static List<string> BuildDirectorHardRules(AiFoundryChatMemoryDto memory)
    {
        var items = new List<string>();
        if (memory.PreferAutonomousCompletion)
            items.Add("Не останавливаться на промежуточных шагах и не просить лишнее одобрение пользователя.");
        if (IsDirectFinalResultIntent(memory.LatestExplicitInstruction))
            items.Add("Нельзя откатывать запрос обратно в plan-loop, если пользователь просит уже готовый результат.");
        if (IsStartOverFromScratchIntent(memory.LatestExplicitInstruction))
            items.Add("Старые blueprint/bridge-plan нужно отбросить, если пользователь просит повторить с нуля.");
        if (RequiresFirstTaskStyleEvidence(memory))
            items.Add("Перед генерацией нужно открыть первое задание курса как эталон стиля.");
        var anchorConcept = DetectAnchorConcept(memory);
        var anchorLabel = AnchorConceptLabel(anchorConcept);
        if (RequestsPreAnchorScaffolding(memory, anchorConcept))
            items.Add($"Это серия подготовительных задач ДО первого {anchorLabel}: нужно подвести к теме через понятные маленькие шаги и видимый результат, но без явного {anchorLabel} в самих условиях.");
        else if (AllowsExplicitAnchorOnboarding(memory, concept: anchorConcept))
            items.Add($"Это не абстрактные мостики до темы {anchorLabel}: нужна серия маленьких программ, которые пошагово учат самому использованию {anchorLabel} в стиле первого дружелюбного задания.");
        var requestedCount = ExtractRequestedProposalCount(memory, new JsonObject());
        if (requestedCount.HasValue)
            items.Add($"Количество новых задач должно быть ровно {requestedCount.Value}.");
        else if (RequestsMorePrograms(memory))
            items.Add("Не схлопывай серию в 2-3 пункта: пользователь просит побольше маленьких программ.");
        var strictPlacement = ResolveStrictRequestedPlacement(memory, new JsonObject());
        if (!string.IsNullOrWhiteSpace(strictPlacement.HumanSummary))
            items.Add($"Точку вставки нельзя сдвигать: {strictPlacement.HumanSummary}.");
        if (ShouldAvoidExplicitAnchorBeforeAnchor(memory, anchorConcept))
            items.Add($"В промежуточных задачах до темы {anchorLabel} нельзя преждевременно вводить {anchorLabel}.");
        else if (AllowsExplicitAnchorOnboarding(memory, concept: anchorConcept))
            items.Add(string.Equals(anchorConcept, "if", StringComparison.OrdinalIgnoreCase)
                ? "В этой серии можно и нужно постепенно вводить сам if, затем if/else, но без резкого прыжка в сухую теорию или олимпиадный стиль."
                : $"В этой серии можно и нужно постепенно вводить сам {anchorLabel}, но без резкого прыжка в сухую теорию или олимпиадный стиль.");
        return items.Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToList();
    }

    private static string? BuildDirectorSummary(AiFoundryChatMemoryDto memory)
    {
        var hardRules = BuildDirectorHardRules(memory);
        if (hardRules.Count == 0)
            return null;
        return "Дирижёр следит за выполнением запроса: " + string.Join(" ", hardRules.Take(4));
    }

    private static AiFoundryChatToolCallDto EnsureToolCallCourseId(AiFoundryChatToolCallDto toolCall, Guid courseId)
    {
        var args = ParseArgumentsObject(toolCall.ArgumentsJson);
        if (!ReadGuid(args, "courseId").HasValue)
            args["courseId"] = courseId.ToString();
        return new AiFoundryChatToolCallDto
        {
            Name = toolCall.Name,
            Reason = toolCall.Reason,
            ArgumentsJson = args.ToJsonString(),
        };
    }

    private static AiFoundryChatToolCallDto? BuildDirectorEnforcedToolCall(
        AiFoundryChatSession session,
        List<AiFoundryChatMessageDto> messages,
        string? assistantText,
        AiFoundryChatMemoryDto memory,
        string? latestGoal,
        string? latestIntentKind)
    {
        var courseId = session.CourseId;
        if (!courseId.HasValue)
            return null;

        var focus = ShortenSingleLine(latestGoal ?? assistantText ?? memory.LatestExplicitInstruction ?? string.Empty, 240);
        var strictPlacement = ResolveStrictRequestedPlacement(memory, new JsonObject());
        var needsFirstTaskEvidence = RequiresFirstTaskStyleEvidence(memory) && !InspectionContainsFirstTask(memory.LastCourseInspection);
        var needsAnchorNeighborhood = strictPlacement.AfterAssignmentId.HasValue && !InspectionContainsAssignment(memory.LastCourseInspection, strictPlacement.AfterAssignmentId.Value);
        var needsInspection = memory.LastCourseInspection == null || memory.LastCourseInspection.CourseId != courseId.Value || needsFirstTaskEvidence || needsAnchorNeighborhood;

        if (needsInspection)
        {
            return new AiFoundryChatToolCallDto
            {
                Name = "inspect_course_assignments",
                Reason = needsFirstTaskEvidence
                    ? "Дирижёр: сначала нужно открыть первое задание как эталон и соседние задания вокруг anchor, иначе дальнейшие шаги будут основаны на догадках."
                    : "Дирижёр: сначала нужно подтвердить anchor и соседние задания по реальным условиям, а не тащить старый план по памяти.",
                ArgumentsJson = JsonSerializer.Serialize(new
                {
                    courseId = courseId.Value,
                    query = focus,
                    aroundAssignmentId = strictPlacement.AfterAssignmentId,
                    window = strictPlacement.AfterAssignmentId.HasValue ? 5 : 3,
                    limitAssignments = needsFirstTaskEvidence ? 28 : 24,
                    includeFirstTaskStyleAnchor = needsFirstTaskEvidence,
                }, JsonOptions),
            };
        }

        if ((memory.PreferAutonomousCompletion || IsDirectFinalResultIntent(memory.LatestExplicitInstruction))
            && memory.CurrentDraftBlueprint != null
            && memory.CurrentDraftBlueprint.Proposals.Count > 0)
        {
            return new AiFoundryChatToolCallDto
            {
                Name = "finalize_chat_blueprint",
                Reason = "Дирижёр: blueprint уже собран, а пользователь просит конечный результат без approval-loop, значит нужно идти в финализацию.",
                ArgumentsJson = JsonSerializer.Serialize(new
                {
                    courseId = courseId.Value,
                    proposalIds = memory.CurrentDraftBlueprint.Proposals.Select(x => x.Id).ToList(),
                    instructionStrictness = memory.InstructionStrictness,
                    enableSelfCheck = true,
                }, JsonOptions),
            };
        }

        if (memory.LastCourseAudit == null && string.Equals(latestIntentKind, "plan", StringComparison.OrdinalIgnoreCase))
        {
            return new AiFoundryChatToolCallDto
            {
                Name = "analyze_course_progression",
                Reason = "Дирижёр: plan/action зависит от свежего аудита. Нельзя строить plan-loop без discovery по текущему фокусу.",
                ArgumentsJson = JsonSerializer.Serialize(new { courseId = courseId.Value, focus }, JsonOptions),
            };
        }

        return new AiFoundryChatToolCallDto
        {
            Name = "advance_agent_stage",
            Reason = "Дирижёр: текущий ответ не выполнил пользовательский план целиком, поэтому нужен ещё один внутренний проход вместо остановки на промежуточном шаге.",
            ArgumentsJson = JsonSerializer.Serialize(new { courseId = courseId.Value, focus }, JsonOptions),
        };
    }

    private static IReadOnlyList<AiFoundryChatToolCallDto>? ApplyDirectorToolCallOverride(
        AiFoundryChatSession session,
        List<AiFoundryChatMessageDto> messages,
        string? assistantText,
        List<AiFoundryChatToolCallDto> toolCalls,
        AiFoundryChatMemoryDto memory,
        string? latestGoal,
        string? latestIntentKind)
    {
        var courseId = session.CourseId;
        var patched = toolCalls
            .Select(x => courseId.HasValue ? EnsureToolCallCourseId(x, courseId.Value) : x)
            .ToList();

        var directFinal = IsDirectFinalResultIntent(latestGoal) || memory.PreferAutonomousCompletion;
        var hardReset = IsStartOverFromScratchIntent(latestGoal) || IsAutonomousReworkIntent(latestGoal);
        var planOnlyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "prepare_bridge_plan", "show_bridge_plan", "revise_bridge_plan" };
        var approvalLoopNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "show_chat_blueprint", "revise_chat_blueprint" };
        var allPlanOnly = patched.Count > 0 && patched.All(x => !string.IsNullOrWhiteSpace(x.Name) && planOnlyNames.Contains(x.Name.Trim()));
        var asksForApproval = !string.IsNullOrWhiteSpace(assistantText) && assistantText.Contains("одобря", StringComparison.OrdinalIgnoreCase);
        var violatesReset = hardReset && patched.Any(x => !string.IsNullOrWhiteSpace(x.Name) && (planOnlyNames.Contains(x.Name.Trim()) || approvalLoopNames.Contains(x.Name.Trim())));
        var violatesDirectFinal = directFinal && (allPlanOnly || asksForApproval || patched.Any(x => !string.IsNullOrWhiteSpace(x.Name) && approvalLoopNames.Contains(x.Name.Trim())));

        if (!violatesReset && !violatesDirectFinal)
            return patched;

        var enforced = BuildDirectorEnforcedToolCall(session, messages, assistantText, memory, latestGoal, latestIntentKind);
        if (enforced == null)
            return patched;

        return new List<AiFoundryChatToolCallDto> { enforced };
    }

    private static AiFoundryChatMemoryDto ApplyInstructionDrivenStateReset(AiFoundryChatMemoryDto previous, string? latestGoal, string? latestIntentKind)
    {
        previous ??= new AiFoundryChatMemoryDto();
        var autonomousRework = IsAutonomousReworkIntent(latestGoal);
        var restartFromScratch = IsStartOverFromScratchIntent(latestGoal);
        var dropBlueprint = string.Equals(latestIntentKind, "drop-blueprint", StringComparison.OrdinalIgnoreCase);
        if (!autonomousRework && !restartFromScratch && !dropBlueprint)
            return previous;

        previous.CurrentDraftBlueprint = null;
        previous.LastBridgePlan = null;

        if (restartFromScratch)
        {
            previous.LastCourseInspection = null;
            previous.LastCourseAudit = null;
        }

        if (previous.AgentState != null)
        {
            previous.AgentState.CurrentStage = "reset-requested";
            previous.AgentState.StageSummary = "Старый workflow отброшен по новой явной инструкции пользователя.";
            previous.AgentState.NextSuggestedAction = null;
            previous.AgentState.BlockerSummary = null;
            previous.AgentState.OpenQuestions = new List<string>();
            previous.AgentState.EvidenceLedger = new List<string>();
            previous.AgentState.RiskFlags = new List<string>();
            previous.AgentState.DecisionCandidates = new List<AiFoundryAgentActionHintDto>();
            previous.AgentState.PlanSteps = new List<AiFoundryAgentPlanStepDto>();
        }

        return previous;
    }

    private static List<int> InferPlanItemIndexesFromText(string? text)
    {
        var low = (text ?? string.Empty).ToLowerInvariant();
        var result = new List<int>();
        void add(int value)
        {
            if (value >= 1 && value <= 24 && !result.Contains(value))
                result.Add(value);
        }
        if (Regex.IsMatch(low, @"\b(перв(ый|ое|ым|ую)|1[- ]?(й|ая|ое)?)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) add(1);
        if (Regex.IsMatch(low, @"\b(втор(ой|ое|ым|ую)|2[- ]?(й|ая|ое)?)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) add(2);
        if (Regex.IsMatch(low, @"\b(трет(ий|ье|ьим|ью)|3[- ]?(й|ья|ье)?)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) add(3);
        if (Regex.IsMatch(low, @"\b(четв(ёрт|ерт)(ый|ое|ым|ую)|4[- ]?(й|ая|ое)?)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) add(4);
        return result;
    }

    private static AiFoundryAgentStateDto BuildChatAgentState(
        AiFoundryChatMemoryDto previous,
        IReadOnlyList<string> recentGoals,
        IReadOnlyList<string> recentActions,
        string? nextAgentStep,
        string? latestIntentKind,
        string? latestTeachingScript)
    {
        var latestGoal = recentGoals.Count > 0 ? recentGoals[^1] : previous.AgentState?.UserIntentSummary;
        var intentSummary = recentGoals.Count > 0
            ? ShortenSingleLine(recentGoals[^1], 220)
            : ShortenSingleLine(previous.AgentState?.UserIntentSummary ?? string.Empty, 220);
        latestIntentKind ??= previous.LatestIntentKind ?? DetectLatestIntentKind(latestGoal);
        latestTeachingScript ??= previous.LatestTeachingScript;

        var objectiveKind = DetermineAgentObjectiveKind(latestGoal, latestIntentKind, previous.CurrentDraftBlueprint != null && previous.CurrentDraftBlueprint.Proposals.Count > 0);
        var remediationIntent = string.Equals(objectiveKind, "course-gap-remediation", StringComparison.OrdinalIgnoreCase);
        var diagnosticAuditIntent = string.Equals(objectiveKind, "course-diagnostics", StringComparison.OrdinalIgnoreCase)
            || string.Equals(latestIntentKind, "audit", StringComparison.OrdinalIgnoreCase)
            || IsDiagnosticGapAuditIntent(latestGoal);
        var hasBlueprint = previous.CurrentDraftBlueprint != null && previous.CurrentDraftBlueprint.Proposals.Count > 0;
        var hasAudit = previous.LastCourseAudit != null;
        var hasInspection = previous.LastCourseInspection != null && previous.LastCourseInspection.Assignments.Count > 0;
        var hasBridgePlan = previous.LastBridgePlan != null && previous.LastBridgePlan.Items.Count > 0;

        var learnerProfile = BuildLearnerProfileSnapshot(string.Join(" ", recentGoals), intentSummary ?? string.Empty, latestTeachingScript);
        var constraints = BuildGenerationConstraintsSnapshot(string.Join(" ", recentGoals), intentSummary ?? string.Empty, latestTeachingScript);
        var styleHints = new List<string>();
        if (previous.LastCourseAudit != null)
            styleHints.AddRange(previous.LastCourseAudit.StyleHints ?? new List<string>());
        if (!diagnosticAuditIntent && previous.LastBridgePlan != null)
            styleHints.AddRange(previous.LastBridgePlan.StyleHints ?? new List<string>());
        styleHints = styleHints
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => ShortenSingleLine(x, 120))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToList();

        var placementCandidates = new List<AiFoundryAgentPlacementCandidateDto>();
        var explicitAfterAssignmentId = recentGoals
            .Reverse()
            .Select(goal => ExtractGuidFromText(goal))
            .FirstOrDefault(id => id.HasValue);
        if (explicitAfterAssignmentId.HasValue)
        {
            var explicitAfterTitle = previous.LastCourseInspection?.Assignments.FirstOrDefault(x => x.Id == explicitAfterAssignmentId.Value)?.Title
                ?? previous.LastBridgePlan?.Items.FirstOrDefault(x => x.AfterAssignmentId == explicitAfterAssignmentId.Value)?.AfterAssignmentTitle
                ?? previous.LastCourseAudit?.Findings.FirstOrDefault(x => x.AfterAssignmentId == explicitAfterAssignmentId.Value)?.AfterAssignmentTitle;
            placementCandidates.Add(new AiFoundryAgentPlacementCandidateDto
            {
                Source = "user-anchor",
                Concept = "explicit-placement",
                AfterAssignmentId = explicitAfterAssignmentId,
                AfterAssignmentTitle = explicitAfterTitle,
                Reason = "Пользователь уже выбрал конкретный afterAssignmentId в этой сессии.",
                TaskCount = 6,
                Difficulty = 1,
                TaskFormat = "guided-walkthrough",
                TitleHint = explicitAfterTitle,
            });
        }
        if (!diagnosticAuditIntent && previous.LastBridgePlan != null)
        {
            var preferred = previous.LastBridgePlan.Items.Where(x => x.Confirmed && !x.Rejected).ToList();
            if (preferred.Count == 0)
                preferred = previous.LastBridgePlan.Items.Where(x => !x.Rejected).ToList();
            placementCandidates.AddRange(preferred.Take(6).Select(x => new AiFoundryAgentPlacementCandidateDto
            {
                Source = "bridge-plan",
                Concept = x.Concept,
                AfterAssignmentId = x.AfterAssignmentId,
                AfterAssignmentTitle = x.AfterAssignmentTitle,
                BeforeAssignmentTitle = x.BeforeAssignmentTitle,
                Reason = ShortenSingleLine(x.Reason, 160),
                TaskCount = x.TaskCount,
                Difficulty = x.Difficulty,
                TaskFormat = x.Index <= 2 ? "guided-walkthrough" : "exercise",
                TitleHint = x.TitleHint,
            }));
        }
        else if ((remediationIntent || !diagnosticAuditIntent) && previous.LastCourseAudit != null)
        {
            placementCandidates.AddRange(previous.LastCourseAudit.Findings.Take(6).Select(x => new AiFoundryAgentPlacementCandidateDto
            {
                Source = "course-audit",
                Concept = x.Concept,
                AfterAssignmentId = x.AfterAssignmentId,
                AfterAssignmentTitle = x.AfterAssignmentTitle,
                BeforeAssignmentTitle = x.BeforeAssignmentTitle,
                Reason = ShortenSingleLine(x.Reason, 160),
                TaskCount = x.SuggestedTaskCount,
                Difficulty = Math.Clamp(x.SuggestedDifficulty ?? 1, 1, 3),
                TitleHint = BuildBridgeTitleHint(x, previous.LastCourseAudit?.TitleExamples ?? new List<string>()),
            }));
        }

        if (!diagnosticAuditIntent && !placementCandidates.Any(x => x.AfterAssignmentId.HasValue))
        {
            var inspectionFallback = BuildInspectionPlacementCandidate(previous.LastCourseInspection);
            if (inspectionFallback != null)
                placementCandidates.Add(inspectionFallback);
        }

        var firstPlacement = placementCandidates.FirstOrDefault(x => x.AfterAssignmentId.HasValue);
        var workflowKind = "conversation";
        var currentStage = "idle";
        var preferDirectGeneration = string.Equals(latestIntentKind, "generate", StringComparison.OrdinalIgnoreCase) || !string.IsNullOrWhiteSpace(latestTeachingScript);
        var autonomousRework = IsAutonomousReworkIntent(latestGoal);
        if (hasBlueprint && !autonomousRework)
        {
            workflowKind = "chat-blueprint";
            currentStage = previous.CurrentDraftBlueprint!.ApprovedForDraft ? "blueprint-finalized" : "blueprint-review";
        }
        else if (hasBlueprint && autonomousRework)
        {
            workflowKind = "generation";
            currentStage = previous.CurrentDraftBlueprint!.ApprovedForDraft ? "blueprint-finalized" : "blueprint-rework";
        }
        else if (remediationIntent)
        {
            workflowKind = "course-remediation";
            currentStage = !hasAudit
                ? "discover"
                : !hasInspection
                    ? "verify"
                    : !hasBridgePlan
                        ? "propose"
                        : "solution-ready";
        }
        else if (diagnosticAuditIntent)
        {
            workflowKind = "course-audit";
            currentStage = previous.LastCourseAudit != null ? "audit-ready" : "audit-requested";
        }
        else if (preferDirectGeneration && previous.LastBridgePlan != null)
        {
            workflowKind = "bridge-generation";
            currentStage = "generation-requested";
        }
        else if (string.Equals(latestIntentKind, "revise-plan", StringComparison.OrdinalIgnoreCase) && previous.LastBridgePlan != null)
        {
            workflowKind = "bridge-planning";
            currentStage = "bridge-revision-requested";
        }
        else if (previous.LastBridgePlan != null)
        {
            workflowKind = "bridge-planning";
            currentStage = string.Equals(previous.LastBridgePlan.Status, "confirmed", StringComparison.OrdinalIgnoreCase)
                || previous.LastBridgePlan.Items.Any(x => x.Confirmed && !x.Rejected)
                ? "bridge-ready"
                : "bridge-draft";
        }
        else if (previous.LastCourseInspection != null)
        {
            workflowKind = "course-inspection";
            currentStage = "inspection-ready";
        }
        else if (previous.LastCourseAudit != null)
        {
            workflowKind = "course-audit";
            currentStage = "audit-ready";
        }
        else if (recentActions.Any(x => x.Contains("batch", StringComparison.OrdinalIgnoreCase)))
        {
            workflowKind = "direct-series-generation";
            currentStage = "batch-queued";
        }

        var readyForGeneration = hasBlueprint && previous.CurrentDraftBlueprint?.ApprovedForDraft == true
            || (!diagnosticAuditIntent && previous.LastBridgePlan != null && (preferDirectGeneration || string.Equals(previous.LastBridgePlan.Status, "confirmed", StringComparison.OrdinalIgnoreCase) || previous.LastBridgePlan.Items.Any(x => x.Confirmed && !x.Rejected)));

        var objectiveSummary = objectiveKind switch
        {
            "course-gap-remediation" => "Сначала найти реальные пробелы по всему курсу, затем проверить соседние задания, собрать мостики и только потом переходить к генерации.",
            "course-diagnostics" => "Проверить курс на скрытые педагогические косяки и подтвердить их по реальным соседним заданиям.",
            "bridge-planning" => "Собрать практичный план мостиков и точек вставки перед генерацией.",
            "chat-blueprint" => "Согласовать с пользователем конкретные условия будущих задач прямо в чате перед финальной генерацией.",
            "course-inspection" => "Открыть реальные задания курса и сверить последовательность, стиль и место вставки.",
            "generation" => "Подготовить и довести задачи до генерации без потери пользовательского замысла.",
            _ => intentSummary ?? string.Empty,
        };

        var subtasks = BuildAgentSubtasks(objectiveKind, hasAudit, hasInspection, hasBridgePlan, hasBlueprint, readyForGeneration, previous.CurrentDraftBlueprint?.ApprovedForDraft == true);
        var stageSummary = subtasks.FirstOrDefault(x => string.Equals(x.Status, "current", StringComparison.OrdinalIgnoreCase))?.Summary
            ?? subtasks.FirstOrDefault(x => string.Equals(x.Status, "pending", StringComparison.OrdinalIgnoreCase))?.Summary
            ?? nextAgentStep;
        var evidenceLedger = BuildAgentEvidenceLedger(previous, objectiveKind, hasAudit, hasInspection, hasBridgePlan, hasBlueprint);
        var openQuestions = BuildAgentOpenQuestions(previous, objectiveKind, latestIntentKind, hasAudit, hasInspection, hasBridgePlan, hasBlueprint, readyForGeneration, intentSummary ?? string.Empty, previous.PreferAutonomousCompletion);
        var riskFlags = BuildAgentRiskFlags(previous, objectiveKind, latestIntentKind, hasAudit, hasInspection, hasBridgePlan, hasBlueprint, readyForGeneration);
        var completionCriteria = BuildAgentCompletionCriteria(objectiveKind, hasAudit, hasInspection, hasBridgePlan, hasBlueprint, readyForGeneration, previous.CurrentDraftBlueprint?.ApprovedForDraft == true, previous.PreferAutonomousCompletion);
        var decisionCandidates = BuildAgentDecisionCandidates(objectiveKind, currentStage, hasAudit, hasInspection, hasBridgePlan, hasBlueprint, readyForGeneration, previous.CurrentDraftBlueprint?.ApprovedForDraft == true, nextAgentStep);
        var confidencePercent = EstimateAgentConfidencePercent(objectiveKind, hasAudit, hasInspection, hasBridgePlan, hasBlueprint, readyForGeneration, previous.CurrentDraftBlueprint?.ApprovedForDraft == true, openQuestions.Count, riskFlags.Count);
        var confidenceReason = BuildAgentConfidenceReason(objectiveKind, confidencePercent, openQuestions, riskFlags);
        var blockerSummary = BuildAgentBlockerSummary(openQuestions, riskFlags, confidencePercent, objectiveKind);
        var needsClarification = ShouldAgentAskClarifyingQuestion(confidencePercent, openQuestions, riskFlags, currentStage, objectiveKind, previous.PreferAutonomousCompletion);
        var autonomyMode = DetermineAgentAutonomyMode(needsClarification, confidencePercent, openQuestions, readyForGeneration, hasBlueprint, previous.PreferAutonomousCompletion);
        var planSteps = BuildAgentPlanSteps(objectiveKind, currentStage, hasAudit, hasInspection, hasBridgePlan, hasBlueprint, readyForGeneration, previous.CurrentDraftBlueprint?.ApprovedForDraft == true, blockerSummary, decisionCandidates);
        var selfCritique = BuildAgentSelfCritique(objectiveKind, openQuestions, riskFlags, decisionCandidates, hasBlueprint, readyForGeneration);

        return new AiFoundryAgentStateDto
        {
            WorkflowKind = workflowKind,
            CurrentStage = currentStage,
            UserIntentSummary = intentSummary ?? string.Empty,
            ObjectiveKind = objectiveKind,
            ObjectiveSummary = objectiveSummary,
            StageSummary = string.IsNullOrWhiteSpace(stageSummary) ? null : ShortenSingleLine(stageSummary, 220),
            LearnerAudience = Convert.ToString(learnerProfile["audience"]) ?? "general",
            PedagogyMode = Convert.ToBoolean(learnerProfile["preferGuidedWalkthroughs"]) || Convert.ToBoolean(learnerProfile["explainLikeChild"]) ? "guided-simple" : "standard",
            NextSuggestedAction = string.IsNullOrWhiteSpace(nextAgentStep) ? null : ShortenSingleLine(nextAgentStep, 120),
            LatestIntentKind = latestIntentKind,
            ConfidencePercent = confidencePercent,
            ConfidenceReason = string.IsNullOrWhiteSpace(confidenceReason) ? null : ShortenSingleLine(confidenceReason, 180),
            SelfCritique = string.IsNullOrWhiteSpace(selfCritique) ? null : ShortenSingleLine(selfCritique, 220),
            BlockerSummary = string.IsNullOrWhiteSpace(blockerSummary) ? null : ShortenSingleLine(blockerSummary, 180),
            NeedsClarification = needsClarification,
            AutonomyMode = autonomyMode,
            PreferDirectGeneration = preferDirectGeneration,
            DirectorSummary = BuildDirectorSummary(previous),
            DirectorNextRequiredAction = string.IsNullOrWhiteSpace(nextAgentStep) ? null : ShortenSingleLine(nextAgentStep, 120),
            DirectorHardRules = BuildDirectorHardRules(previous),
            PlacementAfterAssignmentId = firstPlacement?.AfterAssignmentId,
            PlacementAfterAssignmentTitle = firstPlacement?.AfterAssignmentTitle,
            HasCourseAudit = hasAudit,
            HasCourseInspection = hasInspection,
            HasBridgePlan = hasBridgePlan,
            ReadyForGeneration = readyForGeneration,
            ActiveGoals = recentGoals.Take(4).ToList(),
            ActiveConstraints = ((constraints["mustStayBeforeConcepts"] as List<string>) ?? new List<string>())
                .Concat((constraints["avoidConcepts"] as List<string>) ?? new List<string>())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(6)
                .ToList(),
            StyleHints = styleHints,
            EvidenceLedger = evidenceLedger,
            OpenQuestions = openQuestions,
            RiskFlags = riskFlags,
            CompletionCriteria = completionCriteria,
            Subtasks = subtasks,
            PlanSteps = planSteps,
            DecisionCandidates = decisionCandidates,
            PlacementCandidates = placementCandidates.Take(6).ToList(),
        };
    }

    private static string DetermineAgentObjectiveKind(string? latestGoal, string? latestIntentKind, bool hasBlueprint)
    {
        var autonomousRework = IsAutonomousReworkIntent(latestGoal);
        if (hasBlueprint && !autonomousRework && !string.Equals(latestIntentKind, "generate", StringComparison.OrdinalIgnoreCase))
            return "chat-blueprint";
        if (string.Equals(latestIntentKind, "show-blueprint", StringComparison.OrdinalIgnoreCase)
            || string.Equals(latestIntentKind, "revise-blueprint", StringComparison.OrdinalIgnoreCase)
            || string.Equals(latestIntentKind, "finalize-blueprint", StringComparison.OrdinalIgnoreCase))
            return "chat-blueprint";
        if (string.Equals(latestIntentKind, "remediation", StringComparison.OrdinalIgnoreCase) || IsCourseGapRemediationIntent(latestGoal))
            return "course-gap-remediation";
        if (string.Equals(latestIntentKind, "audit", StringComparison.OrdinalIgnoreCase) || IsDiagnosticGapAuditIntent(latestGoal))
            return "course-diagnostics";
        if (autonomousRework || IsDirectFinalResultIntent(latestGoal) || AiGenerationScenarioPolicy.LooksLikeScenarioGenerationIntent(latestGoal))
            return "generation";
        if (string.Equals(latestIntentKind, "inspect", StringComparison.OrdinalIgnoreCase))
            return "course-inspection";
        if (string.Equals(latestIntentKind, "plan", StringComparison.OrdinalIgnoreCase)
            || string.Equals(latestIntentKind, "show-plan", StringComparison.OrdinalIgnoreCase)
            || string.Equals(latestIntentKind, "revise-plan", StringComparison.OrdinalIgnoreCase))
            return "bridge-planning";
        if (string.Equals(latestIntentKind, "generate", StringComparison.OrdinalIgnoreCase))
            return "generation";
        return "conversation";
    }

    private static List<AiFoundryAgentSubtaskDto> BuildAgentSubtasks(
        string objectiveKind,
        bool hasAudit,
        bool hasInspection,
        bool hasBridgePlan,
        bool hasBlueprint,
        bool readyForGeneration,
        bool blueprintApproved)
    {
        var items = new List<AiFoundryAgentSubtaskDto>();
        void Add(string key, string title, string status, string summary)
            => items.Add(new AiFoundryAgentSubtaskDto { Key = key, Title = title, Status = status, Summary = summary });

        if (string.Equals(objectiveKind, "course-gap-remediation", StringComparison.OrdinalIgnoreCase))
        {
            Add("discover-gaps", "Найти пробелы", hasAudit ? "done" : "current", "Собрать реальные педагогические разрывы и резкие вводы новых тем по всему курсу.");
            Add("verify-context", "Проверить соседние задания", !hasAudit ? "pending" : (hasInspection ? "done" : "current"), "Открыть реальные задания вокруг проблемных точек и подтвердить, что пробелы не ложные.");
            Add("propose-solutions", "Предложить решения", !hasAudit || !hasInspection ? "pending" : (hasBridgePlan ? "done" : "current"), "Собрать мостики, точки вставки и педагогические решения для найденных дыр.");
            Add("draft-solutions", "Перейти к генерации", !hasBridgePlan ? "pending" : (hasBlueprint || readyForGeneration ? (blueprintApproved ? "done" : "current") : "current"), "Либо показать решения в чате для согласования, либо после одобрения переходить к draft/generation.");
            return items;
        }

        if (string.Equals(objectiveKind, "course-diagnostics", StringComparison.OrdinalIgnoreCase))
        {
            Add("audit-course", "Провести аудит", hasAudit ? "done" : "current", "Проверить курс на скрытые prerequisite-ошибки и слишком резкий ввод новых конструкций.");
            Add("inspect-neighbors", "Открыть соседние задания", !hasAudit ? "pending" : (hasInspection ? "done" : "current"), "Проверить конкретные задания вокруг спорных мест, чтобы подтвердить или снять проблему.");
            Add("summarize-findings", "Сформулировать вывод", !hasAudit ? "pending" : "current", "Свести подтверждённые косяки в короткий и практичный итог для пользователя.");
            return items;
        }

        if (string.Equals(objectiveKind, "chat-blueprint", StringComparison.OrdinalIgnoreCase))
        {
            Add("blueprint-chat", "Согласовать условия", hasBlueprint ? "done" : "current", "Собрать или уточнить примерные условия задач прямо в чате.");
            Add("blueprint-finalize", "Финализировать", !hasBlueprint ? "pending" : (blueprintApproved ? "done" : "current"), "После явного одобрения превратить chat blueprint в полноценные draft-черновики.");
            return items;
        }

        if (string.Equals(objectiveKind, "bridge-planning", StringComparison.OrdinalIgnoreCase))
        {
            Add("audit-or-inspect", "Опора на курс", hasAudit || hasInspection ? "done" : "current", "Подготовить аудит или inspection, чтобы план строился не на догадках.");
            Add("bridge-plan", "Собрать план", hasBridgePlan ? "done" : "current", "Зафиксировать точки вставки, микроцели и формат мостиков.");
            Add("confirm-plan", "Показать и уточнить", !hasBridgePlan ? "pending" : "current", "Проверить план вместе с пользователем перед генерацией.");
            return items;
        }

        if (string.Equals(objectiveKind, "course-inspection", StringComparison.OrdinalIgnoreCase))
        {
            Add("inspect-course", "Показать задания", hasInspection ? "done" : "current", "Открыть реальные задания курса и дать пользователю материал для точного обсуждения.");
            return items;
        }

        if (string.Equals(objectiveKind, "generation", StringComparison.OrdinalIgnoreCase))
        {
            Add("shape-request", "Уточнить форму задачи", hasBlueprint || hasBridgePlan ? "done" : "current", "Понять, нужно ли сначала обсуждение в чате, bridge plan или можно идти прямо в draft.");
            Add("generate", "Перейти к генерации", readyForGeneration ? "current" : "pending", "Запустить генерацию только после того, как замысел и структура уже достаточно ясны.");
            return items;
        }

        Add("understand-request", "Понять запрос", "current", "Уточнить цель пользователя и выбрать следующий полезный шаг без лишнего workflow.");
        return items;
    }

    private static List<string> BuildAgentEvidenceLedger(
        AiFoundryChatMemoryDto previous,
        string objectiveKind,
        bool hasAudit,
        bool hasInspection,
        bool hasBridgePlan,
        bool hasBlueprint)
    {
        var evidence = new List<string>();
        if (hasAudit && previous.LastCourseAudit != null)
            evidence.Add($"Есть аудит курса: {ShortenSingleLine(previous.LastCourseAudit.Summary, 140)}");
        if (hasInspection && previous.LastCourseInspection != null)
        {
            evidence.Add($"Есть inspection по реальным заданиям: {previous.LastCourseInspection.Assignments.Count} элементов вокруг спорных мест.");
            if (previous.LastCourseInspection.Observations.Count > 0)
                evidence.Add($"Подтверждено inspection: {ShortenSingleLine(previous.LastCourseInspection.Observations[0], 140)}");
        }
        if (hasBridgePlan && previous.LastBridgePlan != null)
            evidence.Add($"Есть план решений: {previous.LastBridgePlan.Items.Count} точек вставки или мостиков.");
        if (hasBlueprint && previous.CurrentDraftBlueprint != null)
            evidence.Add($"Есть согласуемые черновые условия: {previous.CurrentDraftBlueprint.Proposals.Count} вариант(ов), revision {previous.CurrentDraftBlueprint.Revision}.");
        if (string.Equals(objectiveKind, "course-gap-remediation", StringComparison.OrdinalIgnoreCase) && !hasAudit)
            evidence.Add("Пока нет опорного аудита по курсу — агент ещё не подтвердил реальные пробелы.");
        return evidence.Take(6).ToList();
    }

    private static List<string> BuildAgentOpenQuestions(
        AiFoundryChatMemoryDto previous,
        string objectiveKind,
        string? latestIntentKind,
        bool hasAudit,
        bool hasInspection,
        bool hasBridgePlan,
        bool hasBlueprint,
        bool readyForGeneration,
        string intentSummary,
        bool preferAutonomousCompletion)
    {
        var items = new List<string>();
        if (string.Equals(objectiveKind, "course-gap-remediation", StringComparison.OrdinalIgnoreCase))
        {
            if (!hasAudit)
                items.Add("Нужно сначала подтвердить пробелы по курсу, а не генерировать на ощущениях.");
            if (hasAudit && !hasInspection)
                items.Add("Нужно открыть реальные задания, иначе ответ так и останется эвристикой без чтения условий.");
            if (hasInspection && !hasBridgePlan)
                items.Add("Нужно превратить найденные пробелы в конкретные решения и точки вставки.");
            if (hasBridgePlan && !hasBlueprint)
                items.Add("Нужно показать пользователю примерные условия задач до финальной генерации.");
        }
        else if (string.Equals(objectiveKind, "chat-blueprint", StringComparison.OrdinalIgnoreCase))
        {
            if (!hasBlueprint)
                items.Add("Нужно собрать хотя бы один осмысленный вариант условия прямо в чате.");
            else if (previous.CurrentDraftBlueprint?.ApprovedForDraft != true && !preferAutonomousCompletion)
                items.Add("Нужно явное одобрение пользователя перед финализацией в draft.");
        }
        else if (string.Equals(objectiveKind, "generation", StringComparison.OrdinalIgnoreCase) && !readyForGeneration)
        {
            items.Add("Замысел пока недостаточно зафиксирован: не хватает опорного blueprint или plan-контекста.");
        }
        if (string.IsNullOrWhiteSpace(intentSummary))
            items.Add("Текущий запрос пользователя слишком размытый — нужен более чёткий фокус.");
        return items.Distinct(StringComparer.OrdinalIgnoreCase).Take(6).ToList();
    }

    private static List<string> BuildAgentRiskFlags(
        AiFoundryChatMemoryDto previous,
        string objectiveKind,
        string? latestIntentKind,
        bool hasAudit,
        bool hasInspection,
        bool hasBridgePlan,
        bool hasBlueprint,
        bool readyForGeneration)
    {
        var items = new List<string>();
        if (string.Equals(objectiveKind, "course-gap-remediation", StringComparison.OrdinalIgnoreCase) && !hasInspection && hasAudit)
            items.Add("Есть риск принять эвристический аудит за истину без чтения реальных условий.");
        if (string.Equals(objectiveKind, "course-diagnostics", StringComparison.OrdinalIgnoreCase) && !hasInspection && hasAudit)
            items.Add("Есть риск принять эвристический аудит за истину без чтения реальных условий.");
        if (hasInspection && hasAudit && previous.LastCourseInspection?.Observations.Any(x => x.Contains("обучалк", StringComparison.OrdinalIgnoreCase) || x.Contains("пошагов", StringComparison.OrdinalIgnoreCase)) == true)
            items.Add("Есть риск повторять старый аудит даже после того, как inspection уже подтвердил обратное по реальным условиям.");
        if (string.Equals(objectiveKind, "chat-blueprint", StringComparison.OrdinalIgnoreCase) && hasBlueprint && previous.CurrentDraftBlueprint?.ApprovedForDraft != true)
            items.Add("Есть риск слишком рано финализировать условия без последних правок пользователя.");
        if (string.Equals(latestIntentKind, "generate", StringComparison.OrdinalIgnoreCase) && !readyForGeneration)
            items.Add("Есть риск перепрыгнуть к генерации раньше, чем зафиксирован учебный замысел.");
        if (hasBlueprint && previous.CurrentDraftBlueprint?.Proposals.Count > 1)
            items.Add("Есть риск смешать соседние варианты, если править условия слишком абстрактно.");
        return items.Distinct(StringComparer.OrdinalIgnoreCase).Take(5).ToList();
    }

    private static List<string> BuildAgentCompletionCriteria(
        string objectiveKind,
        bool hasAudit,
        bool hasInspection,
        bool hasBridgePlan,
        bool hasBlueprint,
        bool readyForGeneration,
        bool blueprintApproved,
        bool preferAutonomousCompletion)
    {
        var items = new List<string>();
        if (string.Equals(objectiveKind, "course-gap-remediation", StringComparison.OrdinalIgnoreCase))
        {
            items.Add(hasAudit ? "Аудит курса собран." : "Нужен аудит курса.");
            items.Add(hasInspection ? "Соседние задания проверены." : "Нужно проверить соседние задания.");
            items.Add(hasBridgePlan ? "Решения и точки вставки собраны." : "Нужно собрать решения и точки вставки.");
            items.Add(hasBlueprint || readyForGeneration ? "Можно переходить к обсуждению задач." : "Нужно показать пользователю примерные условия.");
            return items;
        }
        if (string.Equals(objectiveKind, "chat-blueprint", StringComparison.OrdinalIgnoreCase))
        {
            items.Add(hasBlueprint ? "Варианты условий сохранены." : "Нужно сохранить варианты условий.");
            items.Add(blueprintApproved || preferAutonomousCompletion
                ? "Blueprint можно доводить до финального результата без отдельного UX-одобрения."
                : "Нужно явное одобрение перед финализацией.");
            return items;
        }
        items.Add(readyForGeneration ? "Контекст достаточен для следующего шага." : "Контекст ещё не дотянут до следующего шага.");
        return items.Take(4).ToList();
    }

    private static string BuildAgentBlockerSummary(
        IReadOnlyList<string> openQuestions,
        IReadOnlyList<string> riskFlags,
        int confidencePercent,
        string objectiveKind)
    {
        if (openQuestions.Count > 0)
            return openQuestions[0];
        if (confidencePercent < 35 && riskFlags.Count > 0)
            return riskFlags[0];
        if (confidencePercent < 30)
            return string.Equals(objectiveKind, "conversation", StringComparison.OrdinalIgnoreCase)
                ? "Слишком мало опорных фактов: лучше сузить запрос, чем спешить с действием."
                : "Контекст пока слабый: следующему шагу нужна более надёжная опора.";
        return string.Empty;
    }

    private static bool ShouldAgentAskClarifyingQuestion(
        int confidencePercent,
        IReadOnlyList<string> openQuestions,
        IReadOnlyList<string> riskFlags,
        string currentStage,
        string objectiveKind,
        bool preferAutonomousCompletion)
    {
        if (preferAutonomousCompletion)
        {
            if (confidencePercent < 18)
                return true;
            if (openQuestions.Count > 0 && confidencePercent < 42)
                return true;
            return riskFlags.Count >= 3 && confidencePercent < 45;
        }
        if (openQuestions.Count > 0 && confidencePercent < 65)
            return true;
        if (confidencePercent < 25)
            return true;
        if (string.Equals(objectiveKind, "conversation", StringComparison.OrdinalIgnoreCase) && confidencePercent < 40)
            return true;
        if (string.Equals(currentStage, "blueprint-review", StringComparison.OrdinalIgnoreCase) && openQuestions.Count > 0)
            return true;
        return riskFlags.Count >= 2 && confidencePercent < 55;
    }

    private static string DetermineAgentAutonomyMode(
        bool needsClarification,
        int confidencePercent,
        IReadOnlyList<string> openQuestions,
        bool readyForGeneration,
        bool hasBlueprint,
        bool preferAutonomousCompletion)
    {
        if (preferAutonomousCompletion)
        {
            if (readyForGeneration)
                return "execute-ready";
            if (confidencePercent >= 45 || hasBlueprint)
                return "self-directed";
            if (needsClarification && confidencePercent < 18)
                return "ask-first";
            return openQuestions.Count > 0 ? "cautious-self-directed" : "self-directed";
        }
        if (needsClarification)
            return "ask-first";
        if (readyForGeneration)
            return "execute-ready";
        if (hasBlueprint || confidencePercent >= 70)
            return "guided-proactive";
        if (openQuestions.Count > 0)
            return "cautious";
        return "guided";
    }

    private static List<AiFoundryAgentPlanStepDto> BuildAgentPlanSteps(
        string objectiveKind,
        string currentStage,
        bool hasAudit,
        bool hasInspection,
        bool hasBridgePlan,
        bool hasBlueprint,
        bool readyForGeneration,
        bool blueprintApproved,
        string blockerSummary,
        IReadOnlyList<AiFoundryAgentActionHintDto> decisionCandidates)
    {
        var items = new List<AiFoundryAgentPlanStepDto>();
        void Add(string key, string title, string status, string summary, string? action = null, string? success = null, string? blockedBy = null)
            => items.Add(new AiFoundryAgentPlanStepDto
            {
                Key = key,
                Title = title,
                Status = status,
                Summary = summary,
                RecommendedAction = action,
                SuccessSignal = success,
                BlockedBy = blockedBy,
            });

        string? blocker = string.IsNullOrWhiteSpace(blockerSummary) ? null : blockerSummary;
        if (string.Equals(objectiveKind, "course-gap-remediation", StringComparison.OrdinalIgnoreCase))
        {
            Add("discover", "Discovery по курсу", hasAudit ? "done" : "current", "Собрать реальные педагогические пробелы по всему курсу.", "analyze_course_progression", "Есть findings аудита по курсу.", !hasAudit ? blocker : null);
            Add("verify", "Проверка соседних заданий", !hasAudit ? "pending" : (hasInspection ? "done" : "current"), "Подтвердить пробелы на реальных соседних заданиях.", "inspect_course_assignments", "Есть inspection вокруг спорных точек.", hasAudit || hasInspection ? null : blocker);
            Add("propose", "Сбор решений", !hasInspection ? "pending" : (hasBridgePlan ? "done" : "current"), "Превратить подтверждённые пробелы в решения и точки вставки.", "prepare_bridge_plan", "Есть практичный bridge plan.", hasInspection || hasBridgePlan ? null : blocker);
            Add("shape", "Черновые условия", !hasBridgePlan ? "pending" : (hasBlueprint ? "done" : "current"), "Показать пользователю примерные условия задач и дождаться правок.", hasBlueprint ? "revise_chat_blueprint" : "save_chat_blueprint", "Есть chat blueprint с вариантами.", hasBridgePlan || hasBlueprint ? null : blocker);
            Add("draft", "Финализация в draft", !hasBlueprint ? "pending" : (blueprintApproved ? "done" : "current"), "Только после одобрения перевести условия в draft/generation.", "finalize_chat_blueprint", "Blueprint одобрен и готов к draft.", blueprintApproved ? null : (hasBlueprint ? blocker ?? "Нужно одобрение пользователя перед финализацией." : blocker));
            return items;
        }

        if (string.Equals(objectiveKind, "chat-blueprint", StringComparison.OrdinalIgnoreCase))
        {
            Add("collect", "Собрать варианты", hasBlueprint ? "done" : "current", "Собрать в чате читаемые варианты условий.", "save_chat_blueprint", "Есть хотя бы один вариант условия.", !hasBlueprint ? blocker : null);
            Add("revise", "Довести формулировки", !hasBlueprint ? "pending" : (blueprintApproved ? "done" : "current"), "Уточнить стиль, тесты и scope без потери мысли пользователя.", "revise_chat_blueprint", "Blueprint стал точным и согласованным.", hasBlueprint ? blocker : blocker);
            Add("finalize", "Перевести в draft", !hasBlueprint ? "pending" : (blueprintApproved ? "done" : "current"), "После явного одобрения создать полноценный draft.", "finalize_chat_blueprint", "Есть финализированный draft.", blueprintApproved ? null : (blocker ?? "Нужно явное одобрение пользователя."));
            return items;
        }

        if (string.Equals(objectiveKind, "course-diagnostics", StringComparison.OrdinalIgnoreCase))
        {
            Add("audit", "Целевой аудит", hasAudit ? "done" : "current", "Проверить курс на скрытые prerequisite-ошибки.", "analyze_course_progression", "Есть findings аудита.", !hasAudit ? blocker : null);
            Add("inspect", "Проверить спорные места", !hasAudit ? "pending" : (hasInspection ? "done" : "current"), "Открыть соседние задания вокруг спорных мест.", "inspect_course_assignments", "Есть inspection по соседним заданиям.", hasAudit || hasInspection ? null : blocker);
            Add("summarize", "Свести вывод", !hasAudit ? "pending" : "current", "Дать пользователю короткий и практичный итог без лишней выдумки.", null, "Есть итог по проблемам курса.", null);
            return items;
        }

        if (string.Equals(objectiveKind, "generation", StringComparison.OrdinalIgnoreCase))
        {
            var currentAction = decisionCandidates.FirstOrDefault(x => string.Equals(x.Status, "preferred", StringComparison.OrdinalIgnoreCase))?.Name;
            Add("stabilize", "Зафиксировать замысел", readyForGeneration ? "done" : "current", "Понять, можно ли уже генерировать безопасно.", currentAction, "Замысел и контекст зафиксированы.", !readyForGeneration ? blocker : null);
            Add("generate", "Сгенерировать", readyForGeneration ? "current" : "pending", "Перейти к generation только когда контекст действительно собран.", readyForGeneration ? (currentAction ?? "queue_generate_from_text") : null, "Есть generated draft или batch.", !readyForGeneration ? blocker : null);
            return items;
        }

        if (decisionCandidates.Count > 0)
        {
            foreach (var candidate in decisionCandidates.Take(3))
                Add(candidate.Name, candidate.Name, string.Equals(candidate.Status, "preferred", StringComparison.OrdinalIgnoreCase) ? "current" : "pending", candidate.Why, candidate.Name, candidate.Why, blocker);
            return items;
        }

        Add("understand", "Уточнить цель", string.Equals(currentStage, "idle", StringComparison.OrdinalIgnoreCase) ? "current" : "pending", "Сначала сузить цель пользователя и убрать двусмысленность.", null, "Понятно, что делать дальше.", blocker);
        return items;
    }

    private static List<AiFoundryAgentActionHintDto> BuildAgentDecisionCandidates(
        string objectiveKind,
        string currentStage,
        bool hasAudit,
        bool hasInspection,
        bool hasBridgePlan,
        bool hasBlueprint,
        bool readyForGeneration,
        bool blueprintApproved,
        string? nextAgentStep)
    {
        var items = new List<AiFoundryAgentActionHintDto>();
        void Add(string name, string why, string status = "candidate")
            => items.Add(new AiFoundryAgentActionHintDto { Name = name, Why = why, Status = status });

        if (string.Equals(objectiveKind, "course-gap-remediation", StringComparison.OrdinalIgnoreCase))
        {
            if (!hasAudit)
                Add("analyze_course_progression", "Сначала нужен discovery по курсу, иначе remediation будет строиться на догадках.", "preferred");
            else if (!hasInspection)
                Add("inspect_course_assignments", "Нужно проверить соседние задания вокруг найденных пробелов.", "preferred");
            else if (!hasBridgePlan)
                Add("prepare_bridge_plan", "Пора превратить подтверждённые пробелы в решения и точки вставки.", "preferred");
            else if (!hasBlueprint)
                Add("save_chat_blueprint", "Нужно показать пользователю примерные условия до финальной генерации.", "preferred");
            else if (!blueprintApproved)
                Add("revise_chat_blueprint", "Есть blueprint, но он ещё может требовать последних правок.", "candidate");
            else
                Add("finalize_chat_blueprint", "Условия уже согласованы и готовы к переходу в draft.", "preferred");
        }
        else if (string.Equals(objectiveKind, "chat-blueprint", StringComparison.OrdinalIgnoreCase))
        {
            if (!hasBlueprint)
                Add("save_chat_blueprint", "Сначала нужно сохранить варианты условий из чата.", "preferred");
            else if (!blueprintApproved)
                Add("revise_chat_blueprint", "Blueprint есть, но его ещё можно точнее довести.", "preferred");
            else
                Add("finalize_chat_blueprint", "Blueprint уже одобрен и готов к финализации.", "preferred");
        }
        else if (string.Equals(objectiveKind, "course-diagnostics", StringComparison.OrdinalIgnoreCase))
        {
            if (!hasAudit)
                Add("analyze_course_progression", "Сначала нужен целевой аудит курса.", "preferred");
            else if (!hasInspection)
                Add("inspect_course_assignments", "Нужно проверить соседние задания вокруг спорных мест.", "candidate");
        }
        else if (readyForGeneration)
        {
            Add("queue_generate_from_text", "Контекст уже достаточно собран для прямой генерации.", "preferred");
        }

        if (!string.IsNullOrWhiteSpace(nextAgentStep) && items.All(x => !string.Equals(x.Name, nextAgentStep, StringComparison.OrdinalIgnoreCase)))
            Add(nextAgentStep, "Старый nextSuggestedAction всё ещё может быть полезной запасной траекторией.", "fallback");
        return items.Take(4).ToList();
    }

    private static int EstimateAgentConfidencePercent(
        string objectiveKind,
        bool hasAudit,
        bool hasInspection,
        bool hasBridgePlan,
        bool hasBlueprint,
        bool readyForGeneration,
        bool blueprintApproved,
        int openQuestionCount,
        int riskCount)
    {
        var score = 25;
        if (hasAudit)
            score += 15;
        if (hasInspection)
            score += 15;
        if (hasBridgePlan)
            score += 15;
        if (hasBlueprint)
            score += 15;
        if (readyForGeneration)
            score += 10;
        if (blueprintApproved)
            score += 5;
        score -= Math.Min(20, openQuestionCount * 6);
        score -= Math.Min(15, riskCount * 5);
        if (string.Equals(objectiveKind, "conversation", StringComparison.OrdinalIgnoreCase))
            score = Math.Min(score, 55);
        return Math.Clamp(score, 10, 95);
    }

    private static string BuildAgentConfidenceReason(string objectiveKind, int confidencePercent, IReadOnlyList<string> openQuestions, IReadOnlyList<string> riskFlags)
    {
        if (confidencePercent >= 75)
            return "Контекст уже достаточно собран, и агент понимает, какой следующий шаг безопаснее всего.";
        if (openQuestions.Count > 0)
            return $"Уверенность ограничена: {ShortenSingleLine(openQuestions[0], 120)}";
        if (riskFlags.Count > 0)
            return $"Есть осторожность из-за риска: {ShortenSingleLine(riskFlags[0], 120)}";
        return string.Equals(objectiveKind, "conversation", StringComparison.OrdinalIgnoreCase)
            ? "Диалог ещё слишком общий: агенту нужно чуть больше опорных фактов."
            : "Агент уже собрал часть контекста, но ещё не закрыл все обязательные шаги.";
    }

    private static string BuildAgentSelfCritique(
        string objectiveKind,
        IReadOnlyList<string> openQuestions,
        IReadOnlyList<string> riskFlags,
        IReadOnlyList<AiFoundryAgentActionHintDto> decisionCandidates,
        bool hasBlueprint,
        bool readyForGeneration)
    {
        if (openQuestions.Count > 0)
            return $"Агент ещё не должен действовать слишком резко: {ShortenSingleLine(openQuestions[0], 140)}";
        if (riskFlags.Count > 0)
            return $"Агенту нужно помнить про риск: {ShortenSingleLine(riskFlags[0], 140)}";
        if (decisionCandidates.Count > 1)
            return "У агента есть несколько правдоподобных следующих шагов; важно выбрать самый доказательный, а не самый быстрый.";
        if (hasBlueprint && !readyForGeneration)
            return "Blueprint уже есть, но он ещё не равен готовности к финальной генерации.";
        return string.Equals(objectiveKind, "conversation", StringComparison.OrdinalIgnoreCase)
            ? "Агенту пока не хватает узкого фокуса, поэтому полезнее уточнить цель, чем спешить с tool call."
            : "Текущее состояние выглядит достаточно собранным для следующего шага без лишнего перескока.";
    }

    private static object BuildBatchAgentStateSnapshot(
        AiFoundryChatMemoryDto memory,
        Guid courseId,
        string prompt,
        string batchKind,
        int requestedCount,
        int difficulty,
        Dictionary<string, object?> learnerProfile,
        Dictionary<string, object?> constraints,
        List<string> styleHints,
        List<Dictionary<string, object?>> placementPlan)
    {
        var existing = memory.AgentState ?? new AiFoundryAgentStateDto();
        return new
        {
            workflowKind = batchKind,
            currentStage = "batch-structured-context",
            userIntentSummary = ShortenSingleLine(prompt, 220),
            objectiveKind = existing.ObjectiveKind,
            objectiveSummary = existing.ObjectiveSummary,
            stageSummary = existing.StageSummary,
            learnerAudience = Convert.ToString(learnerProfile["audience"]) ?? existing.LearnerAudience,
            pedagogyMode = Convert.ToBoolean(learnerProfile["preferGuidedWalkthroughs"]) || Convert.ToBoolean(learnerProfile["explainLikeChild"]) ? "guided-simple" : existing.PedagogyMode,
            nextSuggestedAction = existing.NextSuggestedAction,
            readyForGeneration = placementPlan.Count > 0,
            requestedCount,
            difficulty,
            activeGoals = (memory.RecentGoals ?? new List<string>()).Take(4).ToList(),
            activeConstraints = ((constraints["mustStayBeforeConcepts"] as List<string>) ?? new List<string>())
                .Concat((constraints["avoidConcepts"] as List<string>) ?? new List<string>())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(6)
                .ToList(),
            styleHints = styleHints.Take(8).ToList(),
            selectedPlacementAfterAssignmentId = placementPlan.FirstOrDefault(x => x.ContainsKey("afterAssignmentId"))?["afterAssignmentId"],
            selectedPlacementAfterAssignmentTitle = placementPlan.FirstOrDefault(x => x.ContainsKey("afterAssignmentTitle"))?["afterAssignmentTitle"],
            placementCandidates = placementPlan.Take(8).ToList(),
            sourceCourseId = courseId,
        };
    }

    private static List<Dictionary<string, object?>> BuildPlacementPlanSnapshot(
        AiFoundryCourseAuditDto? audit,
        AiFoundryBridgePlanDto? bridgePlan,
        int requestedCount)
    {
        var items = new List<Dictionary<string, object?>>();
        if (bridgePlan != null)
        {
            var preferred = bridgePlan.Items.Where(x => x.Confirmed && !x.Rejected).ToList();
            if (preferred.Count == 0)
                preferred = bridgePlan.Items.Where(x => !x.Rejected).ToList();
            foreach (var item in preferred.Take(12))
            {
                items.Add(new Dictionary<string, object?>
                {
                    ["source"] = "bridge-plan",
                    ["index"] = item.Index,
                    ["concept"] = item.Concept,
                    ["afterAssignmentId"] = item.AfterAssignmentId,
                    ["afterAssignmentTitle"] = item.AfterAssignmentTitle,
                    ["beforeAssignmentId"] = item.BeforeAssignmentId,
                    ["beforeAssignmentTitle"] = item.BeforeAssignmentTitle,
                    ["reason"] = item.Reason,
                    ["taskCount"] = item.TaskCount,
                    ["difficulty"] = item.Difficulty,
                    ["titleHint"] = item.TitleHint,
                    ["taskFormat"] = item.Index <= 2 ? "guided-walkthrough" : "exercise",
                    ["titleExamples"] = (item.TitleExamples ?? new List<string>()).Take(6).ToList(),
                });
            }
            if (items.Count > 0)
                return items;
        }

        if (audit != null)
        {
            foreach (var finding in audit.Findings.Take(12))
            {
                items.Add(new Dictionary<string, object?>
                {
                    ["source"] = "course-audit",
                    ["concept"] = finding.Concept,
                    ["afterAssignmentId"] = finding.AfterAssignmentId,
                    ["afterAssignmentTitle"] = finding.AfterAssignmentTitle,
                    ["beforeAssignmentId"] = finding.BeforeAssignmentId,
                    ["beforeAssignmentTitle"] = finding.BeforeAssignmentTitle,
                    ["reason"] = finding.Reason,
                    ["taskCount"] = Math.Max(1, finding.SuggestedTaskCount),
                    ["difficulty"] = Math.Max(1, Math.Min(3, finding.SuggestedDifficulty ?? 1)),
                    ["titleHint"] = finding.Concept,
                    ["taskFormat"] = items.Count == 0 ? "guided-walkthrough" : "exercise",
                    ["titleExamples"] = (audit.TitleExamples ?? new List<string>()).Take(6).ToList(),
                });
            }
        }

        if (requestedCount > 10 && items.Count > 0)
        {
            foreach (var item in items)
            {
                if (item.TryGetValue("taskCount", out var countObj) && countObj is int count && count == 1)
                    item["taskCount"] = 2;
            }
        }
        return items;
    }

    private static bool IsPendingAssistant(AiFoundryChatMessageDto message)
        => string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase)
           && string.Equals(message.Status, "processing", StringComparison.OrdinalIgnoreCase)
           && message.PendingJobId.HasValue;

    private static AiFoundryChatMemoryDto BuildMemory(List<AiFoundryChatMessageDto> messages, string? existingMemoryJson = null, int? instructionStrictnessOverride = null)
    {
        var previous = DeserializeMemory(existingMemoryJson);
        var userMessages = messages
            .Where(x => string.Equals(x.Role, "user", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(x.Content))
            .ToList();
        var assistantMessages = messages
            .Where(x => string.Equals(x.Role, "assistant", StringComparison.OrdinalIgnoreCase) && !string.Equals(x.Status, "processing", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var recentGoals = DistinctRecentStrings(userMessages.Select(x => x.Content), 4, 120);
        var recentFiles = DistinctRecentStrings(
            messages.SelectMany(x => x.Attachments ?? new List<AiFoundryChatAttachmentDto>())
                .Select(x => x.OriginalName ?? x.FileKey),
            6,
            80);
        var recentActions = DistinctRecentStrings(
            messages.SelectMany(x => x.ToolCalls ?? new List<AiFoundryChatToolCallDto>())
                .Select(x => DescribeToolName(x.Name)),
            6,
            80);
        var recentEntities = DistinctRecentStrings(
            messages.SelectMany(x => x.ToolResults ?? new List<AiFoundryChatToolResultDto>())
                .Select(DescribeEntity),
            6,
            80);

        var latestGoal = userMessages.LastOrDefault()?.Content;
        var latestIntentKind = DetectLatestIntentKind(latestGoal) ?? previous.LatestIntentKind;
        previous = ApplyInstructionDrivenStateReset(previous, latestGoal, latestIntentKind);
        var latestTeachingScript = ExtractLatestTeachingScript(latestGoal) ?? previous.LatestTeachingScript;
        var latestExplicitInstruction = ShortenMultiline(latestGoal, 900);
        var preferAutonomousCompletion = ShouldPreferAutonomousCompletion(latestGoal, latestIntentKind) || previous.PreferAutonomousCompletion;
        var hasBlueprint = previous.CurrentDraftBlueprint != null && previous.CurrentDraftBlueprint.Proposals.Count > 0;
        var blueprintIntent = string.Equals(latestIntentKind, "generate", StringComparison.OrdinalIgnoreCase)
            || string.Equals(latestIntentKind, "finalize-blueprint", StringComparison.OrdinalIgnoreCase)
            || string.Equals(latestIntentKind, "show-blueprint", StringComparison.OrdinalIgnoreCase)
            || string.Equals(latestIntentKind, "drop-blueprint", StringComparison.OrdinalIgnoreCase);
        var suppressBridgePlanLoop = ShouldSuppressBridgePlanLoop(latestGoal, latestIntentKind, latestTeachingScript) || hasBlueprint || blueprintIntent;
        var instructionStrictness = ClampInstructionStrictness(instructionStrictnessOverride, previous.InstructionStrictness);
        var directorMemorySnapshot = new AiFoundryChatMemoryDto
        {
            LastCourseAudit = previous.LastCourseAudit,
            LastCourseInspection = previous.LastCourseInspection,
            LastBridgePlan = previous.LastBridgePlan,
            CurrentDraftBlueprint = previous.CurrentDraftBlueprint,
            LatestIntentKind = latestIntentKind,
            LatestTeachingScript = latestTeachingScript,
            LatestExplicitInstruction = latestExplicitInstruction,
            PreferAutonomousCompletion = preferAutonomousCompletion,
            SuppressBridgePlanLoop = suppressBridgePlanLoop,
            RecentGoals = recentGoals,
            AgentState = previous.AgentState,
        };
        var directorSummary = BuildDirectorSummary(directorMemorySnapshot);
        var directorHardRules = BuildDirectorHardRules(directorMemorySnapshot);

        var facts = new List<string>();
        if (recentGoals.Count > 0)
            facts.Add($"Последняя цель пользователя: {recentGoals[^1]}");
        if (!string.IsNullOrWhiteSpace(latestTeachingScript) && facts.Count < 6)
            facts.Add($"Последний пользовательский teaching-script: {ShortenSingleLine(latestTeachingScript, 160)}");
        if (preferAutonomousCompletion && facts.Count < 6)
            facts.Add("Пользователь просит автономный проход: не останавливаться после первого шага и не требовать лишнего согласования.");
        if (!string.IsNullOrWhiteSpace(directorSummary) && facts.Count < 6)
            facts.Add(ShortenSingleLine(directorSummary, 160));
        if (recentFiles.Count > 0)
            facts.Add($"В сессии уже использовались файлы: {string.Join(", ", recentFiles.Take(3))}");
        if (recentActions.Count > 0)
            facts.Add($"AI уже запускала: {string.Join(", ", recentActions.Take(3))}");
        if (recentEntities.Count > 0)
            facts.Add($"Последние сущности/результаты: {string.Join(", ", recentEntities.Take(4))}");
        foreach (var previousFact in previous.Facts ?? new List<string>())
        {
            if (facts.Count >= 6)
                break;
            if (!facts.Any(x => string.Equals(x, previousFact, StringComparison.OrdinalIgnoreCase)))
                facts.Add(previousFact);
        }

        var lastAssistantOutcome = assistantMessages.Count == 0 ? null : ShortenSingleLine(assistantMessages[^1].Content, 180);
        var summaryParts = new List<string>();
        if (messages.Count > 0)
            summaryParts.Add($"В этой сессии уже {messages.Count} сообщений.");
        if (recentGoals.Count > 0)
            summaryParts.Add($"Текущая линия разговора: {recentGoals[^1]}.");
        if (!string.IsNullOrWhiteSpace(latestIntentKind))
            summaryParts.Add($"Последний режим запроса: {latestIntentKind}.");
        if (recentActions.Count > 0)
            summaryParts.Add($"Ранее уже использовали действия: {string.Join(", ", recentActions.Take(3))}.");
        if (recentEntities.Count > 0)
            summaryParts.Add($"Последние сущности: {string.Join(", ", recentEntities.Take(3))}.");
        if (!string.IsNullOrWhiteSpace(lastAssistantOutcome))
            summaryParts.Add($"Последний ответ AI: {lastAssistantOutcome}.");
        if (!string.IsNullOrWhiteSpace(latestTeachingScript))
            summaryParts.Add("Пользователь уже дал явный teaching-script/эталон, который нужно сохранять при следующей генерации.");
        if (preferAutonomousCompletion)
            summaryParts.Add("Пользователь ждёт, что агент сам дойдёт до удовлетворяющего результата без лишних пауз на согласование.");
        if (!string.IsNullOrWhiteSpace(directorSummary))
            summaryParts.Add(directorSummary);
        if (previous.CurrentDraftBlueprint != null && previous.CurrentDraftBlueprint.Proposals.Count > 0)
            summaryParts.Add($"В памяти уже есть {previous.CurrentDraftBlueprint.Proposals.Count} согласуемых услов{(previous.CurrentDraftBlueprint.Proposals.Count == 1 ? "ие" : "ий")} из чата, которые можно показать, поправить или превратить в draft.");
        if (hasBlueprint)
            summaryParts.Add("Сейчас основной workflow — согласование условий в чате, а не bridge-планирование.");
        if (!string.IsNullOrWhiteSpace(previous.AgentState?.ObjectiveSummary))
            summaryParts.Add($"Текущая цель агента: {ShortenSingleLine(previous.AgentState.ObjectiveSummary, 140)}.");

        var summary = string.Join(" ", summaryParts).Trim();
        if (string.IsNullOrWhiteSpace(summary))
            summary = !string.IsNullOrWhiteSpace(previous.Summary) ? previous.Summary : "Пока это пустая сессия без накопленной памяти.";

        if (!hasBlueprint && previous.LastCourseAudit != null && facts.Count < 6)
            facts.Add($"Последний аудит курса: {ShortenSingleLine(previous.LastCourseAudit.Summary, 140)}");
        if (previous.CurrentDraftBlueprint != null && previous.CurrentDraftBlueprint.Proposals.Count > 0 && facts.Count < 6)
            facts.Add($"В чате уже согласуются условия: {string.Join(", ", previous.CurrentDraftBlueprint.Proposals.Select(x => ShortenSingleLine(x.Title, 40)).Take(3))}");
        if (!hasBlueprint && previous.LastCourseAudit != null)
            summary = string.Join(" ", new[] { summary, $"Последний аудит курса: {ShortenSingleLine(previous.LastCourseAudit.Summary, 120)}." }.Where(x => !string.IsNullOrWhiteSpace(x))).Trim();
        if (!hasBlueprint && previous.LastCourseInspection != null && facts.Count < 6)
            facts.Add($"Последний просмотр заданий: {ShortenSingleLine(previous.LastCourseInspection.Summary, 140)}");
        if (!hasBlueprint && previous.LastCourseInspection?.Observations.Count > 0 && facts.Count < 6)
            facts.Add($"По реальным условиям уже подтверждено: {ShortenSingleLine(previous.LastCourseInspection.Observations[0], 140)}");
        if (!hasBlueprint && previous.LastCourseInspection != null)
            summary = string.Join(" ", new[] { summary, $"Последний просмотр заданий: {ShortenSingleLine(previous.LastCourseInspection.Summary, 120)}." }.Where(x => !string.IsNullOrWhiteSpace(x))).Trim();
        if (!suppressBridgePlanLoop && !hasBlueprint && previous.LastBridgePlan != null && facts.Count < 6)
            facts.Add($"Последний план мостиков: {ShortenSingleLine(previous.LastBridgePlan.Summary, 140)}");
        if (!suppressBridgePlanLoop && !hasBlueprint && previous.LastBridgePlan != null)
            summary = string.Join(" ", new[] { summary, $"Последний план мостиков: {ShortenSingleLine(previous.LastBridgePlan.Summary, 120)}." }.Where(x => !string.IsNullOrWhiteSpace(x))).Trim();
        var nextAgentStep = SuggestNextAgentStep(new AiFoundryChatMemoryDto
        {
            RecentGoals = recentGoals.ToList(),
            AgentState = previous.AgentState,
            LastCourseAudit = previous.LastCourseAudit,
            LastCourseInspection = previous.LastCourseInspection,
            LastBridgePlan = previous.LastBridgePlan,
            LatestIntentKind = latestIntentKind,
            LatestTeachingScript = latestTeachingScript,
            LatestExplicitInstruction = latestExplicitInstruction,
            SuppressBridgePlanLoop = suppressBridgePlanLoop,
            CurrentDraftBlueprint = previous.CurrentDraftBlueprint,
        });
        if (!string.IsNullOrWhiteSpace(previous.AgentState?.ObjectiveSummary) && facts.Count < 6)
            facts.Add($"Цель агента: {ShortenSingleLine(previous.AgentState.ObjectiveSummary, 140)}");
        if (!string.IsNullOrWhiteSpace(nextAgentStep) && facts.Count < 6)
            facts.Add($"Следующий логичный шаг агента: {nextAgentStep}");
        if (!string.IsNullOrWhiteSpace(nextAgentStep))
            summary = string.Join(" ", new[] { summary, $"Следующий логичный шаг агента: {nextAgentStep}." }.Where(x => !string.IsNullOrWhiteSpace(x))).Trim();

        var memoryContextForAgent = new AiFoundryChatMemoryDto
        {
            LastCourseAudit = previous.LastCourseAudit,
            LastCourseInspection = previous.LastCourseInspection,
            LastBridgePlan = previous.LastBridgePlan,
            AgentState = previous.AgentState,
            CurrentDraftBlueprint = previous.CurrentDraftBlueprint,
            LatestIntentKind = latestIntentKind,
            LatestTeachingScript = latestTeachingScript,
            LatestExplicitInstruction = latestExplicitInstruction,
            RecentGoals = recentGoals.ToList(),
            PreferAutonomousCompletion = preferAutonomousCompletion,
            SuppressBridgePlanLoop = suppressBridgePlanLoop,
        };
        var agentState = BuildChatAgentState(memoryContextForAgent, recentGoals, recentActions, nextAgentStep, latestIntentKind, latestTeachingScript);
        if (!string.IsNullOrWhiteSpace(agentState.CurrentStage) && facts.Count < 6)
            facts.Add($"Стадия агента: {agentState.CurrentStage}");
        if (!string.IsNullOrWhiteSpace(agentState.StageSummary) && facts.Count < 6)
            facts.Add($"Текущий этап: {ShortenSingleLine(agentState.StageSummary, 140)}");
        if (!string.IsNullOrWhiteSpace(agentState.UserIntentSummary))
            summary = string.Join(" ", new[] { summary, $"Каноническая цель агента: {agentState.UserIntentSummary}." }.Where(x => !string.IsNullOrWhiteSpace(x))).Trim();
        if (!string.IsNullOrWhiteSpace(agentState.ObjectiveSummary))
            summary = string.Join(" ", new[] { summary, $"Стратегия агента: {ShortenSingleLine(agentState.ObjectiveSummary, 160)}." }.Where(x => !string.IsNullOrWhiteSpace(x))).Trim();

        return new AiFoundryChatMemoryDto
        {
            Summary = summary,
            InstructionStrictness = instructionStrictness,
            LastActionMode = NormalizeActionMode(previous.LastActionMode),
            PreferAutonomousCompletion = preferAutonomousCompletion,
            Facts = facts.Take(6).ToList(),
            RecentGoals = recentGoals,
            RecentFiles = recentFiles,
            RecentActions = recentActions,
            LatestIntentKind = latestIntentKind,
            LatestExplicitInstruction = latestExplicitInstruction,
            LatestTeachingScript = latestTeachingScript,
            SuppressBridgePlanLoop = suppressBridgePlanLoop,
            ExecutionContractSummary = directorSummary,
            ExecutionHardRules = directorHardRules,
            MessageCount = messages.Count,
            LastUserMessageAtUtc = userMessages.LastOrDefault()?.CreatedAtUtc,
            LastAssistantMessageAtUtc = assistantMessages.LastOrDefault()?.CreatedAtUtc,
            LastCourseAudit = previous.LastCourseAudit,
            LastCourseInspection = previous.LastCourseInspection,
            LastBridgePlan = previous.LastBridgePlan,
            AgentState = agentState,
            CurrentDraftBlueprint = previous.CurrentDraftBlueprint,
            AnnouncedGenerationJobIds = previous.AnnouncedGenerationJobIds ?? new List<Guid>(),
            AnnouncedAssignmentIds = previous.AnnouncedAssignmentIds ?? new List<Guid>(),
        };
    }

    private static AiFoundryChatMemoryDto DeserializeMemory(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new AiFoundryChatMemoryDto();

        try
        {
            return JsonSerializer.Deserialize<AiFoundryChatMemoryDto>(json, JsonOptions) ?? new AiFoundryChatMemoryDto();
        }
        catch
        {
            return new AiFoundryChatMemoryDto();
        }
    }

    private static int ClampInstructionStrictness(int? value, int? fallback = null)
    {
        var resolved = value ?? fallback ?? DefaultInstructionStrictness;
        return Math.Clamp(resolved, 0, 100);
    }

    private static string SerializeMemory(AiFoundryChatMemoryDto memory)
        => JsonSerializer.Serialize(memory ?? new AiFoundryChatMemoryDto(), JsonOptions);

    private static List<string> DistinctRecentStrings(IEnumerable<string?> values, int limit, int maxLen)
    {
        var list = values
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => ShortenSingleLine(x!, maxLen))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return list.Count <= limit ? list : list[^limit..];
    }

    private static string DescribeToolName(string? rawName)
        => (rawName ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "queue_generate_batch" => "прямая серия генерации",
            "analyze_course_progression" => "аудит курса",
            "inspect_course_assignments" => "просмотр заданий курса",
            "prepare_bridge_plan" => "план мостиков по курсу",
            "show_bridge_plan" => "показ плана мостиков",
            "revise_bridge_plan" => "точечная правка плана мостиков",
            "advance_agent_stage" => "автопродолжение агента",
            "queue_generate_bridge_batch" => "прямая генерация мостиков",
            "save_chat_blueprint" => "примерные условия из чата",
            "revise_chat_blueprint" => "правка примерных условий",
            "show_chat_blueprint" => "просмотр примерных условий",
            "finalize_chat_blueprint" => "финализация условий в черновики",
            "drop_chat_blueprint" => "сброс примерных условий",
            "queue_generate_from_text" => "генерация из текста",
            "revise_draft_from_chat" => "правка готового черновика",
            "queue_generate_from_file" => "генерация из файла",
            "queue_validate_draft" => "валидация draft",
            "approve_draft" => "approve draft",
            "reject_draft" => "reject draft",
            "publish_draft" => "publish draft",
            "queue_analyze_assignment" => "анализ задания",
            "queue_review_submission" => "review попытки",
            "queue_review_user" => "risk-review пользователя",
            _ => string.IsNullOrWhiteSpace(rawName) ? "действие" : rawName!.Trim(),
        };

    private static string? DescribeEntity(AiFoundryChatToolResultDto? result)
    {
        if (result == null)
            return null;
        if (result.BatchId.HasValue)
            return $"batch {result.BatchId.Value.ToString()[..8]}";
        if (result.DraftId.HasValue)
            return $"draft {result.DraftId.Value.ToString()[..8]}";
        if (result.AssignmentId.HasValue)
            return $"assignment {result.AssignmentId.Value.ToString()[..8]}";
        if (result.JobId.HasValue)
            return $"job {result.JobId.Value.ToString()[..8]}";
        return string.IsNullOrWhiteSpace(result.Summary) ? null : ShortenSingleLine(result.Summary, 80);
    }

    private static string ShortenSingleLine(string value, int maxLen)
    {
        var normalized = Regex.Replace(value ?? string.Empty, @"\s+", " ").Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            return string.Empty;
        return normalized.Length <= maxLen ? normalized : normalized[..Math.Max(1, maxLen - 1)] + "…";
    }

    private static string ShortenMultiline(string value, int maxLen)
    {
        var normalized = (value ?? string.Empty).Replace("\r\n", "\n").Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            return string.Empty;
        return normalized.Length <= maxLen ? normalized : normalized[..Math.Max(1, maxLen - 1)] + "…";
    }

    private static List<AiFoundryChatAttachmentDto> NormalizeAttachments(List<AiFoundryChatAttachmentDto>? attachments)
    {
        return (attachments ?? new List<AiFoundryChatAttachmentDto>())
            .Where(x => !string.IsNullOrWhiteSpace(x.FileKey))
            .Select(x => new AiFoundryChatAttachmentDto
            {
                FileKey = x.FileKey.Trim(),
                OriginalName = string.IsNullOrWhiteSpace(x.OriginalName) ? null : x.OriginalName.Trim(),
                MimeType = string.IsNullOrWhiteSpace(x.MimeType) ? null : x.MimeType.Trim(),
                PublicUrl = string.IsNullOrWhiteSpace(x.PublicUrl) ? null : x.PublicUrl.Trim(),
                SizeBytes = x.SizeBytes,
                TextExcerpt = string.IsNullOrWhiteSpace(x.TextExcerpt) ? null : x.TextExcerpt.Trim(),
            })
            .ToList();
    }

    private static AiFoundryChatTraceResponseDto BuildTraceResponse(
        AiFoundryChatSession session,
        List<AiFoundryChatMessageDto> messages,
        AiFoundryChatMemoryDto memory,
        List<AiJob> jobs,
        List<AiBatch> batches)
    {
        var events = new List<AiFoundryChatTraceEventDto>();

        events.Add(new AiFoundryChatTraceEventDto
        {
            Id = $"memory-{session.Id:N}",
            TimestampUtc = session.UpdatedAtUtc,
            Kind = "memory",
            Stage = "memory",
            Title = "Session memory snapshot",
            Status = null,
            Summary = TrimTraceSummary(memory.Summary, 320),
            PayloadJson = SafeSerializeTracePayload(memory),
        });

        foreach (var message in messages.OrderBy(x => x.CreatedAtUtc))
        {
            events.Add(new AiFoundryChatTraceEventDto
            {
                Id = $"msg-{message.Id:N}",
                TimestampUtc = message.CreatedAtUtc,
                Kind = string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase) ? "user-message"
                    : string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase) ? "assistant-message"
                    : "system-message",
                Stage = "message",
                Title = string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase) ? "Сообщение пользователя"
                    : string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase) ? "Ответ модели"
                    : "Системное сообщение",
                Status = string.IsNullOrWhiteSpace(message.Status) ? null : message.Status,
                Summary = TrimTraceSummary(message.Content, 420),
                PayloadJson = SafeSerializeTracePayload(new
                {
                    message.Id,
                    message.Role,
                    message.Status,
                    message.Content,
                    attachmentCount = message.Attachments?.Count ?? 0,
                    pendingJobId = message.PendingJobId,
                }),
            });

            foreach (var toolCall in NormalizeTraceToolCalls(message).Select((value, index) => new { value, index }))
            {
                events.Add(new AiFoundryChatTraceEventDto
                {
                    Id = $"call-{message.Id:N}-{toolCall.index}",
                    TimestampUtc = message.CreatedAtUtc,
                    Kind = "tool-call",
                    Stage = "tool-call",
                    Title = string.IsNullOrWhiteSpace(toolCall.value.Name) ? "Tool call" : toolCall.value.Name,
                    ActionName = toolCall.value.Name,
                    Summary = TrimTraceSummary(toolCall.value.Reason, 240),
                    PayloadJson = SafeSerializeTracePayload(new
                    {
                        toolCall.value.Name,
                        toolCall.value.Reason,
                        Arguments = ParseJsonNodeSafely(toolCall.value.ArgumentsJson),
                    }),
                });
            }

            foreach (var toolResult in NormalizeTraceToolResults(message).Select((value, index) => new { value, index }))
            {
                var debugInfo = toolResult.value.DebugInfo;
                var routing = debugInfo?["routing"];
                var resolution = debugInfo?["resolution"];
                var routeMode = routing?["mode"]?.GetValue<string>();
                var rawRouteMode = routing?["rawMode"]?.GetValue<string>();
                var overrideReason = resolution?["overrideReason"]?.GetValue<string>();
                var actionName = toolResult.value.ActionName ?? debugInfo?["actionName"]?.GetValue<string>();

                events.Add(new AiFoundryChatTraceEventDto
                {
                    Id = $"result-{message.Id:N}-{toolResult.index}",
                    TimestampUtc = message.CreatedAtUtc,
                    Kind = "tool-result",
                    Stage = "tool-result",
                    Title = string.IsNullOrWhiteSpace(actionName) ? "Tool result" : actionName,
                    Status = toolResult.value.Status,
                    RouteMode = routeMode,
                    RawRouteMode = rawRouteMode,
                    ActionName = actionName,
                    OverrideReason = string.Equals(overrideReason, "none", StringComparison.OrdinalIgnoreCase) ? null : overrideReason,
                    RelatedEntityType = toolResult.value.JobId.HasValue ? "job"
                        : toolResult.value.BatchId.HasValue ? "batch"
                        : toolResult.value.DraftId.HasValue ? "draft"
                        : toolResult.value.AssignmentId.HasValue ? "assignment"
                        : null,
                    RelatedEntityId = toolResult.value.JobId ?? toolResult.value.BatchId ?? toolResult.value.DraftId ?? toolResult.value.AssignmentId,
                    Summary = TrimTraceSummary(toolResult.value.Summary, 260),
                    PayloadJson = SafeSerializeTracePayload(new
                    {
                        toolResult.value.ActionName,
                        toolResult.value.Status,
                        toolResult.value.Summary,
                        toolResult.value.NavigateTo,
                        toolResult.value.JobId,
                        toolResult.value.BatchId,
                        toolResult.value.DraftId,
                        toolResult.value.AssignmentId,
                        DebugInfo = toolResult.value.DebugInfo,
                    }),
                });

                if (!string.IsNullOrWhiteSpace(routeMode) || !string.IsNullOrWhiteSpace(rawRouteMode))
                {
                    events.Add(new AiFoundryChatTraceEventDto
                    {
                        Id = $"route-{message.Id:N}-{toolResult.index}",
                        TimestampUtc = message.CreatedAtUtc,
                        Kind = "routing",
                        Stage = "routing",
                        Title = string.IsNullOrWhiteSpace(routeMode) ? "Routing" : $"Route: {routeMode}",
                        Status = toolResult.value.Status,
                        RouteMode = routeMode,
                        RawRouteMode = rawRouteMode,
                        ActionName = actionName,
                        OverrideReason = string.Equals(overrideReason, "none", StringComparison.OrdinalIgnoreCase) ? null : overrideReason,
                        Summary = TrimTraceSummary(routing?["preAnchorReason"]?.GetValue<string>() ?? routing?["mode"]?.GetValue<string>(), 220),
                        PayloadJson = SafeSerializeTracePayload(routing),
                    });
                }
            }
        }

        foreach (var job in jobs)
        {
            events.Add(new AiFoundryChatTraceEventDto
            {
                Id = $"job-{job.Id:N}",
                TimestampUtc = job.CreatedAtUtc,
                Kind = "job",
                Stage = string.IsNullOrWhiteSpace(job.StageCode) ? "job" : job.StageCode!,
                Title = string.IsNullOrWhiteSpace(job.Type) ? "AI job" : job.Type,
                Status = job.Status,
                RelatedEntityType = "job",
                RelatedEntityId = job.Id,
                Summary = TrimTraceSummary(job.StageLabel ?? job.ErrorText ?? job.ResultJson, 260),
                PayloadJson = SafeSerializeTracePayload(new
                {
                    job.Id,
                    job.Type,
                    job.Status,
                    job.StageCode,
                    job.StageLabel,
                    job.ResultJson,
                    job.ErrorText,
                    job.TargetEntityType,
                    job.TargetEntityId,
                    job.CreatedAtUtc,
                    job.CompletedAtUtc,
                }),
            });
        }

        foreach (var batch in batches)
        {
            events.Add(new AiFoundryChatTraceEventDto
            {
                Id = $"batch-{batch.Id:N}",
                TimestampUtc = batch.CreatedAtUtc,
                Kind = "batch",
                Stage = string.IsNullOrWhiteSpace(batch.CurrentStage) ? "batch" : batch.CurrentStage!,
                Title = string.IsNullOrWhiteSpace(batch.AssignmentType) ? "AI batch" : $"Batch · {batch.AssignmentType}",
                Status = batch.Status,
                RelatedEntityType = "batch",
                RelatedEntityId = batch.Id,
                Summary = TrimTraceSummary(batch.Prompt, 260),
                PayloadJson = SafeSerializeTracePayload(new
                {
                    batch.Id,
                    batch.Status,
                    batch.CurrentStage,
                    batch.AssignmentType,
                    batch.Mode,
                    batch.RequestedCount,
                    batch.Prompt,
                    batch.CreatedAtUtc,
                    batch.UpdatedAtUtc,
                }),
            });
        }

        events = events
            .OrderBy(x => x.TimestampUtc)
            .ThenBy(x => x.Kind)
            .ToList();

        var toolResultCount = events.Count(x => x.Kind == "tool-result");
        var failedCount = events.Count(x => string.Equals(x.Status, "failed", StringComparison.OrdinalIgnoreCase) || string.Equals(x.Status, "error", StringComparison.OrdinalIgnoreCase));
        var pendingCount = events.Count(x => string.Equals(x.Status, "pending", StringComparison.OrdinalIgnoreCase) || string.Equals(x.Status, "processing", StringComparison.OrdinalIgnoreCase) || string.Equals(x.Status, "running", StringComparison.OrdinalIgnoreCase));
        var routeCount = events.Count(x => x.Kind == "routing");
        var overrideCount = events.Count(x => !string.IsNullOrWhiteSpace(x.OverrideReason));

        return new AiFoundryChatTraceResponseDto
        {
            SessionId = session.Id,
            SessionTitle = string.IsNullOrWhiteSpace(session.Title) ? "Новый AI-чат" : session.Title,
            GeneratedAtUtc = DateTime.UtcNow,
            Summary = new AiFoundryChatTraceSummaryDto
            {
                MessageCount = messages.Count,
                ToolCallCount = events.Count(x => x.Kind == "tool-call"),
                ToolResultCount = toolResultCount,
                RouteCount = routeCount,
                OverrideCount = overrideCount,
                FailedCount = failedCount,
                PendingCount = pendingCount,
                LinkedJobCount = jobs.Count,
                LinkedBatchCount = batches.Count,
            },
            Events = events,
        };
    }

    private static AiFoundryChatLoopDiagnosticsDto BuildLoopDiagnostics(AiFoundryChatTraceResponseDto trace)
    {
        var result = new AiFoundryChatLoopDiagnosticsDto();
        if (trace == null)
            return result;

        result.LinkedAssistantTurnJobs = trace.Events.Count(x => string.Equals(x.Kind, "job", StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.Title, AiFoundryJobTypes.ChatTurn, StringComparison.OrdinalIgnoreCase));
        result.NeedsRevisionCount = trace.Events.Count(x => string.Equals(x.Kind, "tool-result", StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.Status, "needs-revision", StringComparison.OrdinalIgnoreCase));
        result.BlueprintSaveAttempts = trace.Events.Count(x => string.Equals(x.Kind, "tool-call", StringComparison.OrdinalIgnoreCase)
            && (string.Equals(x.ActionName, "save_chat_blueprint", StringComparison.OrdinalIgnoreCase)
                || string.Equals(x.ActionName, "revise_chat_blueprint", StringComparison.OrdinalIgnoreCase)));

        var fingerprints = trace.Events
            .Where(x => string.Equals(x.Kind, "tool-result", StringComparison.OrdinalIgnoreCase)
                && string.Equals(x.Status, "needs-revision", StringComparison.OrdinalIgnoreCase))
            .Select(BuildRevisionFingerprint)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();
        result.LastFingerprint = fingerprints.LastOrDefault();

        if (result.NeedsRevisionCount >= 2)
            result.Findings.Add($"В trace уже {result.NeedsRevisionCount} шагов needs-revision подряд.");
        if (result.BlueprintSaveAttempts >= 2)
            result.Findings.Add($"В этой сессии уже {result.BlueprintSaveAttempts} повторных попыток save/revise_chat_blueprint.");
        if (result.LinkedAssistantTurnJobs >= 4)
            result.Findings.Add($"Сессия породила {result.LinkedAssistantTurnJobs} assistant_chat_turn job — это похоже на авто-цикл.");
        if (fingerprints.Count >= 2 && string.Equals(fingerprints[^1], fingerprints[^2], StringComparison.OrdinalIgnoreCase))
            result.Findings.Add($"Последние validation-ошибки совпадают по категории: {fingerprints[^1]}.");

        result.SuspectedLoop = result.NeedsRevisionCount >= 2
            || result.BlueprintSaveAttempts >= 3
            || result.LinkedAssistantTurnJobs >= 5
            || result.Findings.Count > 0;
        return result;
    }

    private static string BuildRevisionFingerprint(AiFoundryChatToolResultDto? result)
    {
        var summary = (result?.Summary ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(summary))
            return string.Empty;
        if (summary.Contains("подготовка до темы"))
            return "pre-anchor-before-topic";
        if (summary.Contains("точку вставки"))
            return "placement-mismatch";
        if (summary.Contains("стиль первой задачи"))
            return "first-task-style";
        if (summary.Contains("слишком коротк"))
            return "ladder-too-short";
        if (summary.Contains("нельзя уже вводить"))
            return "anchor-mentioned-too-early";
        summary = Regex.Replace(summary, @"из:\s*.*$", string.Empty).Trim();
        return summary.Length <= 96 ? summary : summary[..96];
    }

    private static string BuildRevisionFingerprint(AiFoundryChatTraceEventDto? traceEvent)
    {
        var summary = (traceEvent?.Summary ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(summary))
            return string.Empty;
        if (summary.Contains("подготовка до темы"))
            return "pre-anchor-before-topic";
        if (summary.Contains("точку вставки"))
            return "placement-mismatch";
        if (summary.Contains("стиль первой задачи"))
            return "first-task-style";
        if (summary.Contains("слишком коротк"))
            return "ladder-too-short";
        if (summary.Contains("нельзя уже вводить"))
            return "anchor-mentioned-too-early";
        summary = Regex.Replace(summary, @"из:\s*.*$", string.Empty).Trim();
        return summary.Length <= 96 ? summary : summary[..96];
    }

    private static List<AiFoundryChatToolCallDto> NormalizeTraceToolCalls(AiFoundryChatMessageDto message)
        => message.ToolCalls?.Count > 0 ? message.ToolCalls : (message.ToolCall == null ? new List<AiFoundryChatToolCallDto>() : new List<AiFoundryChatToolCallDto> { message.ToolCall });

    private static List<AiFoundryChatToolResultDto> NormalizeTraceToolResults(AiFoundryChatMessageDto message)
        => message.ToolResults?.Count > 0 ? message.ToolResults : (message.ToolResult == null ? new List<AiFoundryChatToolResultDto>() : new List<AiFoundryChatToolResultDto> { message.ToolResult });

    private static JsonNode? ParseJsonNodeSafely(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            return JsonNode.Parse(json);
        }
        catch
        {
            return null;
        }
    }

    private static string? SafeSerializeTracePayload(object? payload)
    {
        if (payload == null)
            return null;
        try
        {
            return JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonOptions)
            {
                WriteIndented = true,
            });
        }
        catch
        {
            return payload.ToString();
        }
    }

    private static string? TrimTraceSummary(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var text = value.Trim();
        return text.Length <= max ? text : text[..max] + "…";
    }

    private static List<AiFoundryChatMessageDto> DeserializeMessages(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new List<AiFoundryChatMessageDto>();

        try
        {
            var messages = JsonSerializer.Deserialize<List<AiFoundryChatMessageDto>>(json, JsonOptions) ?? new List<AiFoundryChatMessageDto>();
            foreach (var message in messages)
            {
                if ((message.ToolCalls == null || message.ToolCalls.Count == 0) && message.ToolCall != null)
                    message.ToolCalls = new List<AiFoundryChatToolCallDto> { message.ToolCall };
                if ((message.ToolResults == null || message.ToolResults.Count == 0) && message.ToolResult != null)
                    message.ToolResults = new List<AiFoundryChatToolResultDto> { message.ToolResult };
            }
            return messages;
        }
        catch
        {
            return new List<AiFoundryChatMessageDto>();
        }
    }

    private static string SerializeMessages(List<AiFoundryChatMessageDto> messages)
        => JsonSerializer.Serialize(messages ?? new List<AiFoundryChatMessageDto>(), JsonOptions);

    private async Task<Dictionary<Guid, string>> LoadCourseTitleMapAsync(IEnumerable<Guid> courseIds, CancellationToken ct)
    {
        var ids = courseIds.Distinct().ToList();
        if (ids.Count == 0)
            return new Dictionary<Guid, string>();

        return await _db.Courses.AsNoTracking()
            .Where(x => ids.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x.Title, ct);
    }

    private static AiFoundryChatSessionDto MapSession(
        AiFoundryChatSession session,
        IReadOnlyDictionary<Guid, string>? courseMap = null,
        List<AiFoundryChatMessageDto>? parsedMessages = null,
        AiFoundryChatMemoryDto? parsedMemory = null)
        => new()
        {
            Id = session.Id,
            CourseId = session.CourseId,
            CourseTitle = session.CourseId.HasValue && courseMap != null && courseMap.TryGetValue(session.CourseId.Value, out var courseTitle) ? courseTitle : null,
            Title = string.IsNullOrWhiteSpace(session.Title) ? "Новый AI-чат" : session.Title,
            CreatedAtUtc = session.CreatedAtUtc,
            UpdatedAtUtc = session.UpdatedAtUtc,
            Memory = parsedMemory ?? BuildMemory(parsedMessages ?? DeserializeMessages(session.MessagesJson), session.PlanJson),
            Messages = parsedMessages ?? DeserializeMessages(session.MessagesJson),
        };

    private static AiFoundryChatSessionListItemDto MapListItem(AiFoundryChatSession session, IReadOnlyDictionary<Guid, string>? courseMap = null)
    {
        var messages = DeserializeMessages(session.MessagesJson);
        var last = messages.LastOrDefault();
        var memory = BuildMemory(messages, session.PlanJson);
        return new AiFoundryChatSessionListItemDto
        {
            Id = session.Id,
            CourseId = session.CourseId,
            CourseTitle = session.CourseId.HasValue && courseMap != null && courseMap.TryGetValue(session.CourseId.Value, out var courseTitle) ? courseTitle : null,
            Title = string.IsNullOrWhiteSpace(session.Title) ? "Новый AI-чат" : session.Title,
            LastMessagePreview = string.IsNullOrWhiteSpace(last?.Content) ? null : last!.Content.Trim(),
            MemorySummary = string.IsNullOrWhiteSpace(memory.Summary) ? null : memory.Summary,
            MessageCount = messages.Count,
            IsPending = messages.Any(IsPendingAssistant),
            CreatedAtUtc = session.CreatedAtUtc,
            UpdatedAtUtc = session.UpdatedAtUtc,
        };
    }

    private sealed class CourseAuditAssignmentSnapshot
    {
        public Guid Id { get; set; }
        public int Sort { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public int Difficulty { get; set; }
        public string? Type { get; set; }
        public string? AllowedLanguagesCsv { get; set; }
        public AiAssignmentOverviewDto? AiOverview { get; set; }
    }

    private async Task<AiFoundryCourseAuditDto?> AnalyzeCourseProgressionAsync(Guid courseId, string? focus, int? limitAssignments, CancellationToken ct)
    {
        var course = await _db.Courses.AsNoTracking()
            .Where(x => x.Id == courseId)
            .Select(x => new { x.Id, x.Title })
            .FirstOrDefaultAsync(ct);
        if (course == null)
            return null;

        var ordered = await LoadCourseAuditAssignmentsAsync(courseId, limitAssignments, ct);
        if (ordered.Count == 0)
            return null;

        var findings = new List<AiFoundryCourseAuditFindingDto>();
        var seenConcepts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var titleExamples = ordered
            .Select(x => (x.Title ?? string.Empty).Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToList();
        var styleHints = DetectTitleStyleHints(ordered, titleExamples);
        var focusText = (focus ?? string.Empty).Trim();

        for (var i = 0; i < ordered.Count; i++)
        {
            var current = ordered[i];
            var concepts = ExtractCourseConcepts($"{current.Title}\n{ExtractPlainTextFromRichDescription(current.Description)}");
            foreach (var concept in concepts)
            {
                if (!seenConcepts.Add(concept))
                    continue;
                if (!ShouldFlagConcept(concept, focusText))
                    continue;
                if (AssignmentIntroducesConcept(current, concept))
                    continue;

                var after = i > 0 ? ordered[i - 1] : null;
                findings.Add(new AiFoundryCourseAuditFindingDto
                {
                    Concept = concept,
                    AfterAssignmentId = after?.Id,
                    AfterAssignmentTitle = after?.Title,
                    BeforeAssignmentId = current.Id,
                    BeforeAssignmentTitle = current.Title,
                    Reason = BuildFindingReason(concept, current.Title),
                    SuggestedTaskCount = SuggestTaskCountForConcept(concept),
                    SuggestedDifficulty = SuggestDifficultyForConcept(concept, current.Difficulty),
                });
            }
        }

        // Only apply MatchesFocus narrowing for non-diagnostic audits with specific focus.
        // For diagnostic intents ("find gaps", "syntax basics"), known-concept filtering
        // in ShouldFlagConcept is sufficient — double-filtering causes false negatives
        // (e.g. focus token "курса" not matching "курсе" in finding text).
        var isDiagnostic = IsDiagnosticGapAuditIntent(focusText) || FocusWantsSyntaxBasics(focusText)
            || string.IsNullOrWhiteSpace(focusText);
        findings = (isDiagnostic
                ? findings
                : findings.Where(x => MatchesFocus(x, focusText)))
            .Take(12)
            .ToList();

        return new AiFoundryCourseAuditDto
        {
            CourseId = course.Id,
            CourseTitle = course.Title,
            Focus = string.IsNullOrWhiteSpace(focusText) ? null : focusText,
            GeneratedAtUtc = DateTime.UtcNow,
            Summary = findings.Count == 0
                ? $"Полный проход по {ordered.Count} заданиям не дал надёжных точек вставки по текущим эвристикам. Нужен просмотр landmark-заданий и явный выбор anchor."
                : $"Нашла {findings.Count} точек, где стоит вставить bridge-задачи до резкого ввода новой функции или конструкции. Проверено заданий: {ordered.Count}.",
            StyleHints = styleHints,
            TitleExamples = titleExamples,
            Findings = findings,
        };
    }

    private async Task<AiFoundryCourseInspectionDto?> InspectCourseAssignmentsAsync(Guid courseId, string? query, Guid? aroundAssignmentId, int? window, int? limitAssignments, bool includeFirstTaskStyleAnchor, CancellationToken ct)
    {
        var course = await _db.Courses.AsNoTracking()
            .Where(x => x.Id == courseId)
            .Select(x => new { x.Id, x.Title })
            .FirstOrDefaultAsync(ct);
        if (course == null)
            return null;

        var ordered = await LoadCourseAuditAssignmentsAsync(courseId, null, ct);
        if (ordered.Count == 0)
            return null;

        var selected = new List<CourseAuditAssignmentSnapshot>();
        var normalizedQuery = (query ?? string.Empty).Trim();
        var radius = Math.Clamp(window ?? 2, 0, 5);
        var maxItems = Math.Clamp(limitAssignments ?? 18, 6, 30);
        var resolvedAroundAssignmentId = aroundAssignmentId;
        var shouldIncludeFirstTaskStyleAnchor = includeFirstTaskStyleAnchor || RequestsFirstTaskStyleEvidence(normalizedQuery);

        var queryAnchorConcept = DetectAnchorConceptFromText(normalizedQuery);
        if (!resolvedAroundAssignmentId.HasValue && QueryMentionsFirstAnchor(normalizedQuery, queryAnchorConcept))
            resolvedAroundAssignmentId = FindFirstExplicitAnchorAssignment(ordered, queryAnchorConcept)?.Id;

        if (resolvedAroundAssignmentId.HasValue)
        {
            var index = ordered.FindIndex(x => x.Id == resolvedAroundAssignmentId.Value);
            if (index >= 0)
            {
                var from = Math.Max(0, index - radius);
                var take = Math.Min(ordered.Count - from, radius * 2 + 1);
                selected.AddRange(ordered.Skip(from).Take(take));
            }
        }

        if (shouldIncludeFirstTaskStyleAnchor)
            selected.AddRange(ordered.Take(Math.Min(3, maxItems)));

        if (!string.IsNullOrWhiteSpace(normalizedQuery))
        {
            var matches = ordered
                .Select((item, idx) => new { item, idx })
                .Where(x => MatchesInspectionQuery(x.item, normalizedQuery))
                .ToList();
            var indexes = new SortedSet<int>();
            foreach (var match in matches.Take(6))
            {
                var from = Math.Max(0, match.idx - radius);
                var to = Math.Min(ordered.Count - 1, match.idx + radius);
                for (var i = from; i <= to; i++)
                    indexes.Add(i);
            }
            foreach (var idx in indexes)
                selected.Add(ordered[idx]);
        }

        var importantSeeds = SelectImportantInspectionSeeds(ordered, Math.Min(6, maxItems));
        if (selected.Count == 0)
        {
            selected.AddRange(importantSeeds);
            foreach (var idx in BuildInspectionLandmarkIndexes(ordered.Count))
            {
                if (selected.Count >= maxItems)
                    break;
                selected.Add(ordered[idx]);
            }
        }
        else
        {
            foreach (var important in importantSeeds)
            {
                if (selected.Count >= maxItems)
                    break;
                selected.Add(important);
            }

            var landmarkIndexes = BuildInspectionLandmarkIndexes(ordered.Count);
            foreach (var idx in landmarkIndexes.Take(Math.Max(0, maxItems - selected.Count)).Take(6))
            {
                if (selected.Count >= maxItems)
                    break;
                selected.Add(ordered[idx]);
            }
        }

        var selectedSnapshots = selected
            .DistinctBy(x => x.Id)
            .OrderBy(x => x.Sort)
            .Take(maxItems)
            .ToList();

        var overviewMap = await LoadLatestAssignmentOverviewMapAsync(selectedSnapshots.Select(x => x.Id), ct);
        var assignments = selectedSnapshots
            .Select(x => new AiFoundryCourseInspectionAssignmentDto
            {
                Id = x.Id,
                Sort = x.Sort,
                Difficulty = x.Difficulty,
                Title = x.Title,
                DescriptionExcerpt = BuildDescriptionExcerpt(x.Description),
                AiOverview = overviewMap.TryGetValue(x.Id, out var overview) ? overview : null,
            })
            .ToList();
        var observations = BuildInspectionObservations(selectedSnapshots);
        var explicitAnchor = FindFirstExplicitAnchorAssignment(selectedSnapshots, queryAnchorConcept);
        if (explicitAnchor != null && !string.IsNullOrWhiteSpace(queryAnchorConcept))
        {
            observations.Insert(0, $"Первое задание, где явно появляется {queryAnchorConcept}, — «{explicitAnchor.Title}» (sort={explicitAnchor.Sort}, assignmentId={explicitAnchor.Id}). Именно перед ним и нужно ставить обучалки.");
        }

        var rangeText = assignments.Count == 0 ? null : $"sort {assignments.Min(x => x.Sort)}–{assignments.Max(x => x.Sort)}";
        return new AiFoundryCourseInspectionDto
        {
            CourseId = course.Id,
            CourseTitle = course.Title,
            Query = string.IsNullOrWhiteSpace(normalizedQuery) ? null : normalizedQuery,
            AroundAssignmentId = resolvedAroundAssignmentId,
            GeneratedAtUtc = DateTime.UtcNow,
            Summary = assignments.Count == 0
                ? "Подходящих заданий для просмотра не найдено."
                : explicitAnchor != null && !string.IsNullOrWhiteSpace(queryAnchorConcept)
                    ? $"Открыла {assignments.Count} реальных заданий курса ({rangeText}) и нашла первое явное {queryAnchorConcept} в «{explicitAnchor.Title}» (sort={explicitAnchor.Sort}). Теперь можно строить обучалки строго перед этой точкой, а не гадать по аудиту."
                    : $"Открыла {assignments.Count} реальных заданий курса ({rangeText}), чтобы проверить условия без догадок и снять ложные срабатывания аудита.",
            Observations = observations,
            Assignments = assignments,
        };
    }

    private async Task<List<CourseAuditAssignmentSnapshot>> LoadCourseAuditAssignmentsAsync(Guid courseId, int? limitAssignments, CancellationToken ct)
    {
        var ordered = await _db.TaskAssignments.AsNoTracking()
            .Where(x => x.CourseId == courseId)
            .OrderBy(x => x.Sort)
            .ThenBy(x => x.CreatedAt)
            .Select(x => new CourseAuditAssignmentSnapshot
            {
                Id = x.Id,
                Sort = x.Sort,
                Title = x.Title,
                Description = x.Description,
                Difficulty = x.Difficulty,
                Type = x.Type,
                AllowedLanguagesCsv = x.AllowedLanguagesCsv,
            })
            .ToListAsync(ct);

        if (limitAssignments.HasValue && limitAssignments.Value > 0 && ordered.Count > limitAssignments.Value)
            ordered = ordered.Take(limitAssignments.Value).ToList();

        var overviewMap = await LoadLatestAssignmentOverviewMapAsync(ordered.Select(x => x.Id), ct);
        foreach (var snapshot in ordered)
        {
            if (overviewMap.TryGetValue(snapshot.Id, out var overview))
                snapshot.AiOverview = overview;
        }

        return ordered;
    }

    private AiFoundryChatToolCallDto? BuildNextAgentToolCall(Guid courseId, AiFoundryChatSession session, List<AiFoundryChatMessageDto> messages, JsonObject args)
    {
        var memory = DeserializeMemory(session.PlanJson);
        var focus = ReadString(args, "focus") ?? _chatFallbackFocus(messages);
        var latestIntentKind = memory.LatestIntentKind ?? memory.AgentState?.LatestIntentKind ?? DetectLatestIntentKind(focus);
        var hasBlueprint = memory.CurrentDraftBlueprint != null && memory.CurrentDraftBlueprint.Proposals.Count > 0;
        var objectiveKind = !string.IsNullOrWhiteSpace(memory.AgentState?.ObjectiveKind)
            ? memory.AgentState.ObjectiveKind
            : DetermineAgentObjectiveKind(focus, latestIntentKind, hasBlueprint);
        var remediationIntent = string.Equals(objectiveKind, "course-gap-remediation", StringComparison.OrdinalIgnoreCase);
        var diagnosticAuditIntent = string.Equals(objectiveKind, "course-diagnostics", StringComparison.OrdinalIgnoreCase)
            || IsDiagnosticGapAuditIntent(focus);
        var auditMatchesFocus = memory.LastCourseAudit != null && memory.LastCourseAudit.CourseId == courseId && FocusCompatible(memory.LastCourseAudit.Focus, focus);
        var inspectionMatchesFocus = memory.LastCourseInspection != null && memory.LastCourseInspection.CourseId == courseId && memory.LastCourseInspection.Assignments.Count > 0;
        var hasAudit = auditMatchesFocus;
        var hasInspection = inspectionMatchesFocus;
        var hasBridgePlan = memory.LastBridgePlan != null && memory.LastBridgePlan.CourseId == courseId && memory.LastBridgePlan.Items.Count > 0;
        var placementAfterAssignmentId = ResolveRequestedAfterAssignmentId(args, memory)
            ?? memory.LastCourseAudit?.Findings.FirstOrDefault()?.AfterAssignmentId
            ?? memory.AgentState?.PlacementAfterAssignmentId;
        var preferDirectGeneration = memory.SuppressBridgePlanLoop || memory.AgentState?.PreferDirectGeneration == true || string.Equals(latestIntentKind, "generate", StringComparison.OrdinalIgnoreCase);
        var preferAutonomousCompletion = memory.PreferAutonomousCompletion;
        var autonomousRework = IsAutonomousReworkIntent(memory.LatestExplicitInstruction) || IsAutonomousReworkIntent(focus);

        if (string.Equals(latestIntentKind, "inspect", StringComparison.OrdinalIgnoreCase))
        {
            if (hasInspection)
                return null;

            return new AiFoundryChatToolCallDto
            {
                Name = "inspect_course_assignments",
                Reason = "Последний запрос пользователя — посмотреть существующие задания курса, поэтому сначала нужно показать сами задания, а не строить новый план.",
                ArgumentsJson = JsonSerializer.Serialize(new
                {
                    courseId,
                    query = focus,
                    aroundAssignmentId = placementAfterAssignmentId,
                    window = placementAfterAssignmentId.HasValue ? 4 : 0,
                    limitAssignments = 30,
                }, JsonOptions),
            };
        }

        var requestedCount = TryExtractRequestedCount(focus) ?? 1;
        var scenarioProfile = AiGenerationScenarioRouter.Resolve(memory, focus, focus, requestedCount);
        if (string.Equals(latestIntentKind, "generate", StringComparison.OrdinalIgnoreCase)
            && AiGenerationScenarioPolicy.ShouldBypassBlueprint(scenarioProfile, preferAutonomousCompletion || autonomousRework, focus))
        {
            var prompt = RewritePromptForStepByStepSeries(focus ?? BuildFallbackPrompt(messages), memory, requestedCount);
            var sourceText = RewriteSourceTextForStepByStepSeries(focus, memory, requestedCount);
            var titleHint = AiGenerationScenarioPromptAdapter.SuggestTitleHint(memory, prompt, sourceText, requestedCount, null);
            return new AiFoundryChatToolCallDto
            {
                Name = "queue_generate_from_text",
                Reason = "Пользователь явно просит готовый результат без промежуточного согласования, а выбранный сценарий допускает прямую генерацию.",
                ArgumentsJson = JsonSerializer.Serialize(new
                {
                    courseId,
                    assignmentType = "code-test",
                    prompt,
                    sourceText,
                    count = requestedCount,
                    difficulty = 2,
                    titleHint,
                    enableSelfCheck = true,
                }, JsonOptions),
            };
        }

        var blueprintIntent = string.Equals(objectiveKind, "chat-blueprint", StringComparison.OrdinalIgnoreCase)
            || string.Equals(latestIntentKind, "generate", StringComparison.OrdinalIgnoreCase)
            || string.Equals(latestIntentKind, "show-blueprint", StringComparison.OrdinalIgnoreCase)
            || string.Equals(latestIntentKind, "revise-blueprint", StringComparison.OrdinalIgnoreCase)
            || string.Equals(latestIntentKind, "finalize-blueprint", StringComparison.OrdinalIgnoreCase);
        if (blueprintIntent && !hasBlueprint)
        {
            var strictPlacement = ResolveStrictRequestedPlacement(memory, args);
            var needsFirstTaskEvidence = RequiresFirstTaskStyleEvidence(memory) && !InspectionContainsFirstTask(memory.LastCourseInspection);
            var needsAnchorNeighborhood = strictPlacement.AfterAssignmentId.HasValue && !InspectionContainsAssignment(memory.LastCourseInspection, strictPlacement.AfterAssignmentId.Value);

            if (!hasInspection || needsFirstTaskEvidence || needsAnchorNeighborhood)
            {
                var anchorForInspection = strictPlacement.AfterAssignmentId ?? placementAfterAssignmentId;
                return new AiFoundryChatToolCallDto
                {
                    Name = "inspect_course_assignments",
                    Reason = needsFirstTaskEvidence
                        ? "Перед сборкой условий нужно одновременно открыть эталон первой задачи и соседние задания вокруг точки вставки, иначе стиль и anchor будут взяты из догадок."
                        : "Перед сборкой условий нужно открыть реальные соседние задания вокруг точки вставки, чтобы не промахнуться по anchor и учебной лестнице.",
                    ArgumentsJson = JsonSerializer.Serialize(new
                    {
                        courseId,
                        query = focus,
                        aroundAssignmentId = anchorForInspection,
                        window = anchorForInspection.HasValue ? 5 : 3,
                        limitAssignments = needsFirstTaskEvidence ? 28 : 24,
                        includeFirstTaskStyleAnchor = needsFirstTaskEvidence,
                    }, JsonOptions),
                };
            }

            return null;
        }

        if (blueprintIntent && hasBlueprint && memory.CurrentDraftBlueprint != null)
        {
            var blueprint = memory.CurrentDraftBlueprint;
            var strictPlacement = ResolveStrictRequestedPlacement(memory, args);
            var validationArgs = new JsonObject();
            validationArgs["courseId"] = courseId.ToString();
            if (strictPlacement.AfterAssignmentId.HasValue)
            {
                validationArgs["afterAssignmentId"] = strictPlacement.AfterAssignmentId.Value.ToString();
                if (!string.IsNullOrWhiteSpace(strictPlacement.AfterAssignmentTitle))
                    validationArgs["afterAssignmentTitle"] = strictPlacement.AfterAssignmentTitle;
            }
            var blueprintValidation = ValidateChatBlueprintProposals(memory, blueprint.Proposals.ToList(), validationArgs);
            if (blueprintValidation != null)
            {
                var needsFirstTaskEvidence = RequiresFirstTaskStyleEvidence(memory) && !InspectionContainsFirstTask(memory.LastCourseInspection);
                var needsAnchorNeighborhood = strictPlacement.AfterAssignmentId.HasValue && !InspectionContainsAssignment(memory.LastCourseInspection, strictPlacement.AfterAssignmentId.Value);
                if (!hasInspection || needsFirstTaskEvidence || needsAnchorNeighborhood)
                {
                    var anchorForInspection = strictPlacement.AfterAssignmentId ?? placementAfterAssignmentId;
                    return new AiFoundryChatToolCallDto
                    {
                        Name = "inspect_course_assignments",
                        Reason = "Текущий blueprint конфликтует с последней инструкцией пользователя. Нужно открыть эталон и соседние задания заново, прежде чем его править или финализировать.",
                        ArgumentsJson = JsonSerializer.Serialize(new
                        {
                            courseId,
                            query = focus,
                            aroundAssignmentId = anchorForInspection,
                            window = anchorForInspection.HasValue ? 5 : 3,
                            limitAssignments = needsFirstTaskEvidence ? 28 : 24,
                            includeFirstTaskStyleAnchor = needsFirstTaskEvidence,
                        }, JsonOptions),
                    };
                }
                return null;
            }
            var wantsImmediateDraft = string.Equals(latestIntentKind, "finalize-blueprint", StringComparison.OrdinalIgnoreCase)
                || (preferAutonomousCompletion && (string.Equals(latestIntentKind, "generate", StringComparison.OrdinalIgnoreCase) || IsAutonomousReworkIntent(memory.LatestExplicitInstruction)));
            if (wantsImmediateDraft && blueprint.ApprovedForDraft != true)
            {
                return new AiFoundryChatToolCallDto
                {
                    Name = "finalize_chat_blueprint",
                    Reason = "Пользователь просит не зависать на ручном одобрении: blueprint уже собран и теперь его нужно сразу перевести в полноценные draft-черновики.",
                    ArgumentsJson = JsonSerializer.Serialize(new
                    {
                        courseId,
                        proposalIds = blueprint.Proposals.Select(x => x.Id).ToList(),
                        instructionStrictness = memory.InstructionStrictness,
                        enableSelfCheck = true,
                    }, JsonOptions),
                };
            }
        }

        if (remediationIntent)
        {
            if (!hasAudit)
            {
                return new AiFoundryChatToolCallDto
                {
                    Name = "analyze_course_progression",
                    Reason = "Полноценный remediation-проход всегда начинается с нового аудита курса: нужно найти реальные пробелы, а не генерировать решения вслепую.",
                    ArgumentsJson = JsonSerializer.Serialize(new { courseId, focus }, JsonOptions),
                };
            }

            if (!hasInspection)
            {
                return new AiFoundryChatToolCallDto
                {
                    Name = "inspect_course_assignments",
                    Reason = "После аудита remediation-агент должен проверить реальные соседние задания вокруг дыр, чтобы отделить настоящие пробелы от ложных срабатываний.",
                    ArgumentsJson = JsonSerializer.Serialize(new
                    {
                        courseId,
                        query = focus,
                        aroundAssignmentId = placementAfterAssignmentId,
                        window = placementAfterAssignmentId.HasValue ? 5 : 0,
                        limitAssignments = 32,
                    }, JsonOptions),
                };
            }

            if (!hasBridgePlan)
            {
                return new AiFoundryChatToolCallDto
                {
                    Name = "prepare_bridge_plan",
                    Reason = "После discovery и verify remediation-агент должен собрать конкретные решения: мостики, afterAssignmentId и формат задач.",
                    ArgumentsJson = JsonSerializer.Serialize(new { courseId, focus, afterAssignmentId = placementAfterAssignmentId, count = 8 }, JsonOptions),
                };
            }

            var confirmedForRemediation = memory.LastBridgePlan!.Items.Where(x => x.Confirmed && !x.Rejected).Select(x => x.Index).ToList();
            if (preferDirectGeneration && confirmedForRemediation.Count > 0)
            {
                return new AiFoundryChatToolCallDto
                {
                    Name = "queue_generate_from_text",
                    Reason = "Remediation-агент уже собрал и подтвердил решения, поэтому теперь можно переходить к генерации по плану.",
                    ArgumentsJson = JsonSerializer.Serialize(new { courseId, focus, itemIndexes = confirmedForRemediation }, JsonOptions),
                };
            }

            return new AiFoundryChatToolCallDto
            {
                Name = "show_bridge_plan",
                Reason = "Remediation-агент уже собрал решения и теперь должен показать их пользователю для обсуждения, прежде чем идти в генерацию.",
                ArgumentsJson = JsonSerializer.Serialize(new { courseId }, JsonOptions),
            };
        }

        if (diagnosticAuditIntent)
        {
            if (!hasAudit)
            {
                return new AiFoundryChatToolCallDto
                {
                    Name = "analyze_course_progression",
                    Reason = "Новый запрос пользователя — целевой аудит педагогических косяков и скрытых prerequisite-пробелов, поэтому сначала нужно заново проанализировать курс под этот фокус.",
                    ArgumentsJson = JsonSerializer.Serialize(new { courseId, focus }, JsonOptions),
                };
            }

            if (!hasInspection && placementAfterAssignmentId.HasValue)
            {
                return new AiFoundryChatToolCallDto
                {
                    Name = "inspect_course_assignments",
                    Reason = "После аудита полезно открыть соседние задания вокруг проблемной точки, чтобы проверить формулировки и скрытые prerequisite-ошибки в реальном тексте.",
                    ArgumentsJson = JsonSerializer.Serialize(new
                    {
                        courseId,
                        query = focus,
                        aroundAssignmentId = placementAfterAssignmentId,
                        window = 4,
                        limitAssignments = 20,
                    }, JsonOptions),
                };
            }

            return null;
        }

        if (preferDirectGeneration && memory.LastBridgePlan != null && memory.LastBridgePlan.CourseId == courseId && memory.LastBridgePlan.Items.Count > 0)
        {
            var selectedIndexes = memory.LastBridgePlan.Items
                .Where(x => !x.Rejected)
                .OrderBy(x => x.Index)
                .Select(x => x.Index)
                .ToList();
            return new AiFoundryChatToolCallDto
            {
                Name = "queue_generate_from_text",
                Reason = "Последний явный запрос пользователя — не обсуждать план дальше, а перейти к генерации по уже собранным plan items и teaching-script.",
                ArgumentsJson = JsonSerializer.Serialize(new
                {
                    courseId,
                    focus,
                    itemIndexes = selectedIndexes,
                    prompt = memory.LatestTeachingScript ?? memory.LatestExplicitInstruction,
                }, JsonOptions),
            };
        }

        if (string.Equals(latestIntentKind, "plan", StringComparison.OrdinalIgnoreCase))
        {
            if (!hasAudit && !hasInspection && !placementAfterAssignmentId.HasValue)
            {
                return new AiFoundryChatToolCallDto
                {
                    Name = "analyze_course_progression",
                    Reason = "Чтобы собрать осмысленный план, сначала нужен актуальный аудит курса под текущий фокус.",
                    ArgumentsJson = JsonSerializer.Serialize(new { courseId, focus }, JsonOptions),
                };
            }

            if (!hasInspection && !placementAfterAssignmentId.HasValue)
            {
                var firstFinding = memory.LastCourseAudit?.Findings.FirstOrDefault();
                return new AiFoundryChatToolCallDto
                {
                    Name = "inspect_course_assignments",
                    Reason = "Перед планом полезно открыть реальные задания курса вокруг точки вставки, чтобы сверить стиль и последовательность.",
                    ArgumentsJson = JsonSerializer.Serialize(new
                    {
                        courseId,
                        query = focus,
                        aroundAssignmentId = firstFinding?.AfterAssignmentId,
                        window = 4,
                        limitAssignments = 28,
                    }, JsonOptions),
                };
            }
        }

        if (!string.Equals(latestIntentKind, "plan", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(latestIntentKind, "show-plan", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(latestIntentKind, "revise-plan", StringComparison.OrdinalIgnoreCase))
            return null;

        if (memory.LastBridgePlan == null || memory.LastBridgePlan.CourseId != courseId || memory.LastBridgePlan.Items.Count == 0)
        {
            if (!hasAudit)
            {
                return new AiFoundryChatToolCallDto
                {
                    Name = "analyze_course_progression",
                    Reason = "prepare_bridge_plan требует свежий аудит курса. Сначала нужно заново собрать discovery по текущему фокусу, а уже потом строить план.",
                    ArgumentsJson = JsonSerializer.Serialize(new { courseId, focus }, JsonOptions),
                };
            }

            return new AiFoundryChatToolCallDto
            {
                Name = "prepare_bridge_plan",
                Reason = "Есть audit и inspection, значит следующий шаг — зафиксировать подробный план мостиков без повторного анализа.",
                ArgumentsJson = JsonSerializer.Serialize(new { courseId, focus, afterAssignmentId = placementAfterAssignmentId, count = 6 }, JsonOptions),
            };
        }

        var confirmedItems = memory.LastBridgePlan.Items.Where(x => x.Confirmed && !x.Rejected).Select(x => x.Index).ToList();
        if (string.Equals(memory.LastBridgePlan.Status, "confirmed", StringComparison.OrdinalIgnoreCase) || confirmedItems.Count > 0)
        {
            return new AiFoundryChatToolCallDto
            {
                Name = "queue_generate_from_text",
                Reason = "В плане уже есть подтверждённые точки вставки, поэтому можно переходить к bridge-generation.",
                ArgumentsJson = JsonSerializer.Serialize(new { courseId, focus, itemIndexes = confirmedItems }, JsonOptions),
            };
        }

        if (string.Equals(latestIntentKind, "revise-plan", StringComparison.OrdinalIgnoreCase))
        {
            return new AiFoundryChatToolCallDto
            {
                Name = "revise_bridge_plan",
                Reason = "Последний запрос пользователя — точечно поправить уже собранный bridge-plan, не пересобирая его с нуля.",
                ArgumentsJson = JsonSerializer.Serialize(new
                {
                    courseId,
                    itemIndexes = InferPlanItemIndexesFromText(memory.LatestExplicitInstruction),
                    note = memory.LatestExplicitInstruction,
                }, JsonOptions),
            };
        }

        return new AiFoundryChatToolCallDto
        {
            Name = "show_bridge_plan",
            Reason = "План мостиков уже собран, но ещё не подтверждён. Следующий логичный шаг — показать его и обсудить точечные правки.",
            ArgumentsJson = JsonSerializer.Serialize(new { courseId }, JsonOptions),
        };
    }

    private static string? SuggestNextAgentStep(AiFoundryChatMemoryDto memory)
    {
        var latestGoal = memory.RecentGoals?.LastOrDefault() ?? memory.AgentState?.UserIntentSummary;
        var latestIntentKind = memory.LatestIntentKind ?? memory.AgentState?.LatestIntentKind ?? DetectLatestIntentKind(latestGoal);
        var objectiveKind = !string.IsNullOrWhiteSpace(memory.AgentState?.ObjectiveKind)
            ? memory.AgentState.ObjectiveKind
            : DetermineAgentObjectiveKind(latestGoal, latestIntentKind, memory.CurrentDraftBlueprint != null && memory.CurrentDraftBlueprint.Proposals.Count > 0);
        var diagnosticAuditIntent = string.Equals(objectiveKind, "course-diagnostics", StringComparison.OrdinalIgnoreCase);
        var remediationIntent = string.Equals(objectiveKind, "course-gap-remediation", StringComparison.OrdinalIgnoreCase);
        var hasAudit = memory.LastCourseAudit != null;
        var hasInspection = memory.LastCourseInspection != null && memory.LastCourseInspection.Assignments.Count > 0;
        var hasPlacement = memory.AgentState?.PlacementAfterAssignmentId.HasValue == true || (memory.AgentState?.PlacementCandidates?.Any(x => x.AfterAssignmentId.HasValue) == true);
        var hasBridgePlan = memory.LastBridgePlan != null && memory.LastBridgePlan.Items.Count > 0;

        if (remediationIntent)
        {
            if (!hasAudit)
                return "запустить discovery по курсу через analyze_course_progression";
            if (!hasInspection)
                return hasPlacement
                    ? "проверить соседние задания вокруг найденных дыр через inspect_course_assignments"
                    : "открыть опорные задания курса через inspect_course_assignments, чтобы подтвердить найденные пробелы";
            if (!hasBridgePlan)
                return "собрать план решений и мостиков через prepare_bridge_plan";
            if (memory.CurrentDraftBlueprint != null && memory.CurrentDraftBlueprint.Proposals.Count > 0)
                return memory.CurrentDraftBlueprint.ApprovedForDraft ? "дождаться появления draft-черновиков по согласованным условиям" : "показать или поправить примерные условия решений в чате, а потом финализировать их";
            return "показать решения пользователю и только после согласования переходить к генерации";
        }

        if (diagnosticAuditIntent)
        {
            if (!hasAudit)
                return "сначала провести целевой аудит пробелов через analyze_course_progression";
            if (!hasInspection && hasPlacement)
                return "открыть соседние задания вокруг проблемной точки через inspect_course_assignments";
            return "сформулировать конкретные педагогические косяки и только потом решать, нужен ли новый план мостиков";
        }

        if (memory.CurrentDraftBlueprint != null && memory.CurrentDraftBlueprint.Proposals.Count > 0)
        {
            if (memory.PreferAutonomousCompletion && memory.CurrentDraftBlueprint.ApprovedForDraft != true)
                return "самостоятельно исправить blueprint под последнюю инструкцию и только затем финализировать его";
            return memory.CurrentDraftBlueprint.ApprovedForDraft ? "дождаться появления draft-черновиков по согласованным условиям" : "показать или поправить примерные условия из чата, а потом вызвать finalize_chat_blueprint";
        }
        var latestAnchorConcept = DetectAnchorConcept(memory);
        var latestAnchorLabel = AnchorConceptLabel(latestAnchorConcept);
        if (string.Equals(latestIntentKind, "generate", StringComparison.OrdinalIgnoreCase) && ShouldAvoidExplicitAnchorBeforeAnchor(memory, latestAnchorConcept))
            return $"собрать серию подготовительных задач до первого {latestAnchorLabel} через save_chat_blueprint";
        if (string.Equals(latestIntentKind, "generate", StringComparison.OrdinalIgnoreCase) && AllowsExplicitAnchorOnboarding(memory, concept: latestAnchorConcept))
            return $"собрать серию маленьких программ по самому {latestAnchorLabel} через save_chat_blueprint";
        if (string.Equals(latestIntentKind, "generate", StringComparison.OrdinalIgnoreCase) && memory.LastBridgePlan != null && memory.LastBridgePlan.Items.Count > 0)
            return "сгенерировать мостики через queue_generate_from_text";
        if (string.Equals(latestIntentKind, "revise-plan", StringComparison.OrdinalIgnoreCase) && memory.LastBridgePlan != null && memory.LastBridgePlan.Items.Count > 0)
            return "точечно поправить bridge-plan через revise_bridge_plan";
        if (string.Equals(latestIntentKind, "show-plan", StringComparison.OrdinalIgnoreCase) && memory.LastBridgePlan != null && memory.LastBridgePlan.Items.Count > 0)
            return "показать текущий bridge-plan через show_bridge_plan";

        if (string.Equals(latestIntentKind, "inspect", StringComparison.OrdinalIgnoreCase))
            return hasInspection ? null : "показать существующие задания курса через inspect_course_assignments";
        if (!hasAudit && !hasInspection && !hasPlacement)
            return "сначала сделать analyze_course_progression";
        if (!hasInspection && !hasPlacement)
            return "открыть соседние задания через inspect_course_assignments";
        if (memory.LastBridgePlan == null || memory.LastBridgePlan.Items.Count == 0)
            return "собрать план мостиков через prepare_bridge_plan";
        var confirmed = memory.LastBridgePlan.Items.Count(x => x.Confirmed && !x.Rejected);
        if (string.Equals(memory.LastBridgePlan.Status, "confirmed", StringComparison.OrdinalIgnoreCase) || confirmed > 0)
            return "сгенерировать мостики через queue_generate_from_text";
        return memory.SuppressBridgePlanLoop ? "сгенерировать мостики через queue_generate_from_text" : "показать и уточнить план мостиков через show_bridge_plan / revise_bridge_plan";
    }

    private static string? _chatFallbackFocus(List<AiFoundryChatMessageDto> messages)
    {
        var lastUser = messages.LastOrDefault(x => string.Equals(x.Role, "user", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(x.Content))?.Content;
        return string.IsNullOrWhiteSpace(lastUser) ? null : ShortenSingleLine(lastUser, 240);
    }

    private static AiFoundryChatMemoryDto WithLastCourseAudit(AiFoundryChatMemoryDto memory, AiFoundryCourseAuditDto report)
    {
        memory ??= new AiFoundryChatMemoryDto();
        memory.LastCourseAudit = report;
        return memory;
    }

    private static AiFoundryChatMemoryDto WithLastCourseInspection(AiFoundryChatMemoryDto memory, AiFoundryCourseInspectionDto report)
    {
        memory ??= new AiFoundryChatMemoryDto();
        memory.LastCourseInspection = report;
        return memory;
    }

    private static AiFoundryChatMemoryDto WithLastBridgePlan(AiFoundryChatMemoryDto memory, AiFoundryBridgePlanDto plan)
    {
        memory ??= new AiFoundryChatMemoryDto();
        memory.LastBridgePlan = plan;
        return memory;
    }

    private static AiFoundryChatMemoryDto WithCurrentDraftBlueprint(AiFoundryChatMemoryDto memory, AiFoundryChatDraftBlueprintDto? blueprint)
    {
        memory ??= new AiFoundryChatMemoryDto();
        memory.CurrentDraftBlueprint = blueprint;
        return memory;
    }

    private static List<Guid> ReadGuidList(JsonObject args, string propertyName)
    {
        var result = new List<Guid>();
        if (args[propertyName] is not JsonArray arr)
            return result;
        foreach (var node in arr)
        {
            if (node == null)
                continue;
            if (Guid.TryParse(node.ToString(), out var value))
                result.Add(value);
        }
        return result.Distinct().ToList();
    }

    private static AiFoundryChatToolResultDto? ValidateChatBlueprintProposals(AiFoundryChatMemoryDto memory, List<AiFoundryChatDraftProposalDto> proposals, JsonObject args)
    {
        var issues = new List<string>();
        var requestedCount = ExtractRequestedProposalCount(memory, args);
        if (requestedCount.HasValue && proposals.Count != requestedCount.Value)
            issues.Add($"Пользователь просил {requestedCount.Value} задач(и), а в blueprint сейчас {proposals.Count}.");
        if (!requestedCount.HasValue && RequestsMorePrograms(memory) && proposals.Count < 6)
            issues.Add("Пользователь просил побольше маленьких программ, а текущий blueprint всё ещё слишком короткий. Нужна более длинная лесенка, хотя бы 6 шагов.");
        if (RequestsTutorialLadder(memory) && proposals.Count < 5)
            issues.Add("Пользователь просил именно обучающую лесенку, а не пару разрозненных примеров. Нужна серия минимум из 5 маленьких шагов.");
        if (RequestsTutorialLadder(memory))
        {
            var dryTitles = proposals.Where(ProposalUsesDryOlympiadTone).Select(x => x.Title).Take(4).ToList();
            if (dryTitles.Count >= Math.Max(1, proposals.Count / 2))
                issues.Add($"Пользователь просил задачки-обучалки, а не сухие code-test формулировки. Перепиши в дружелюбный пошаговый формат со scaffold вроде «Давай...» и «Следуй шагам:»: {string.Join(", ", dryTitles)}.");

            var walkthroughCount = proposals.Count(ProposalLooksLikeFriendlyWalkthrough);
            if (walkthroughCount == 0)
                issues.Add("В текущем blueprint вообще не видно формата лесенки: нет дружелюбного вступления и пошагового scaffold. Нужны именно задачки-обучалки, а не короткие голые условия.");
        }

        var strictAnchor = ResolveStrictRequestedPlacement(memory, args);
        if (strictAnchor.AfterAssignmentId.HasValue)
        {
            var mismatch = proposals
                .Where(x => x.PlacementAfterAssignmentId != strictAnchor.AfterAssignmentId || !string.Equals((x.PlacementAfterTitle ?? string.Empty).Trim(), (strictAnchor.AfterAssignmentTitle ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase))
                .Select(x => x.Title)
                .Take(3)
                .ToList();
            if (mismatch.Count > 0)
                issues.Add($"Пользователь явно задал точку вставки: {strictAnchor.HumanSummary}. Нельзя переносить варианты в другое место курса.");
        }

        if (RequiresFirstTaskStyleEvidence(memory) && !InspectionContainsFirstTask(memory.LastCourseInspection))
            issues.Add("Пользователь просил стиль 'как первая задача', но в текущем просмотре нет самой первой задачи или раннего эталона. Сначала открой первое задание курса и только потом сохраняй blueprint.");

        var anchorConcept = DetectAnchorConcept(memory);
        var anchorLabel = AnchorConceptLabel(anchorConcept);
        var routingDecision = ResolveBlueprintRoutingDecision(memory, args, proposals, anchorConcept);
        if (routingDecision.ShouldAvoidExplicitAnchor)
        {
            var explicitAnchorTitles = proposals
                .Where(x => ProposalUsesExplicitAnchor(x, anchorConcept))
                .Select(x => x.Title)
                .Take(3)
                .ToList();
            if (explicitAnchorTitles.Count > 0)
                issues.Add($"Это подготовка ДО темы {anchorLabel}, поэтому в промежуточных задачах нельзя уже вводить {anchorLabel}. Убери явное упоминание конструкции из: {string.Join(", ", explicitAnchorTitles)}.");
        }
        else if (routingDecision.AllowsExplicitOnboarding)
        {
            var explicitAnchorTitles = proposals.Where(x => ProposalUsesExplicitAnchor(x, anchorConcept)).Select(x => x.Title).Take(3).ToList();
            if (explicitAnchorTitles.Count == 0)
                issues.Add($"Пользователь просит лесенку по {anchorLabel}, поэтому blueprint не должен уезжать в абстрактные мостики без самой темы. Добавь явный {anchorLabel} уже в первых шагах.");

            var abstractTitles = proposals.Where(x => ProposalLooksTooAbstractForAnchorOnboarding(x, anchorConcept)).Select(x => x.Title).Take(4).ToList();
            if (abstractTitles.Count > 0)
                issues.Add($"Для лесенки по {anchorLabel} нельзя подменять тему соседней абстракцией. Сделай эти шаги маленькими программами или упражнениями с видимым результатом: {string.Join(", ", abstractTitles)}.");
        }

        if (RequiresFirstTaskStyleEvidence(memory))
        {
            var dryTitles = proposals.Where(ProposalUsesDryOlympiadTone).Select(x => x.Title).Take(4).ToList();
            if (dryTitles.Count > 0)
                issues.Add($"Пользователь просил стиль первой задачи, а не сухой олимпиадный шаблон. Убери тон «Напишите программу / Дано / Ввод-Вывод» из: {string.Join(", ", dryTitles)}.");
        }

        if (issues.Count == 0)
            return null;

        var result = NeedsRevisionTool("Blueprint пока не удовлетворяет явной инструкции пользователя. " + string.Join(" ", issues));
        result.DebugInfo = BuildBlueprintRoutingDebugInfo("validate_chat_blueprint", memory, args, proposals, issues, routingDecision);
        return result;
    }

    private static BlueprintRoutingDecision ResolveBlueprintRoutingDecision(AiFoundryChatMemoryDto memory, JsonObject args, IReadOnlyList<AiFoundryChatDraftProposalDto> proposals, string? concept)
    {
        var diagnostics = AnalyzeAnchorRouting(memory, concept);
        concept = diagnostics.Concept;
        var hintMode = ReadRoutingHintString(args, "mode") ?? ReadString(args, "anchorRoutingMode") ?? "none";
        var hintConcept = ReadRoutingHintString(args, "concept") ?? ReadString(args, "anchorConcept") ?? concept ?? string.Empty;
        var hintAgreed = ReadRoutingHintBool(args, "agreed") ?? false;
        var latestInstructionLooksLikeAssent = LooksLikeBlueprintAgreementInstruction(memory.LatestExplicitInstruction);
        var inferredProposalOnboarding = ProposalsSuggestAnchorOnboarding(proposals, concept);
        var hintProposalEvidence = ReadRoutingHintBool(args, "proposalEvidence") ?? inferredProposalOnboarding;
        var hintRequestsOnboarding = string.Equals(hintMode, "anchor-onboarding", StringComparison.OrdinalIgnoreCase)
            || string.Equals(hintMode, "onboarding", StringComparison.OrdinalIgnoreCase);
        var conceptMatches = string.IsNullOrWhiteSpace(hintConcept)
            || string.IsNullOrWhiteSpace(concept)
            || string.Equals(hintConcept, concept, StringComparison.OrdinalIgnoreCase);
        var overrideToOnboarding = false;
        var overrideReason = "none";

        if (hintRequestsOnboarding && conceptMatches && hintProposalEvidence && (hintAgreed || latestInstructionLooksLikeAssent))
        {
            overrideToOnboarding = true;
            overrideReason = "worker-onboarding-hint";
        }
        else if (diagnostics.PreAnchorDetected
            && string.Equals(diagnostics.PreAnchorReason, "abrupt-course-gap-fallback", StringComparison.OrdinalIgnoreCase)
            && inferredProposalOnboarding
            && latestInstructionLooksLikeAssent)
        {
            overrideToOnboarding = true;
            overrideReason = "proposal-agreement-override";
        }

        var shouldAvoid = !overrideToOnboarding && ShouldAvoidExplicitAnchorBeforeAnchor(memory, concept);
        var allowsExplicit = overrideToOnboarding || diagnostics.AllowsExplicitOnboarding;
        var effectiveMode = shouldAvoid ? "pre-anchor" : (allowsExplicit ? "anchor-onboarding" : diagnostics.Mode);
        return new BlueprintRoutingDecision
        {
            Diagnostics = diagnostics,
            HintMode = string.IsNullOrWhiteSpace(hintMode) ? "none" : hintMode,
            HintConcept = hintConcept,
            HintAgreed = hintAgreed,
            HintProposalEvidence = hintProposalEvidence,
            InferredProposalOnboarding = inferredProposalOnboarding,
            LatestInstructionLooksLikeAssent = latestInstructionLooksLikeAssent,
            OverrideToOnboarding = overrideToOnboarding,
            OverrideReason = overrideReason,
            ShouldAvoidExplicitAnchor = shouldAvoid,
            AllowsExplicitOnboarding = allowsExplicit,
            EffectiveMode = effectiveMode,
        };
    }

    private static string? ReadRoutingHintString(JsonObject args, string propertyName)
    {
        if (args["routingHint"] is JsonObject hint)
            return ReadString(hint, propertyName);
        return null;
    }

    private static bool? ReadRoutingHintBool(JsonObject args, string propertyName)
    {
        if (args["routingHint"] is not JsonObject hint)
            return null;
        return ReadBool(hint, propertyName);
    }

    private static bool LooksLikeBlueprintAgreementInstruction(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var low = text.ToLowerInvariant();
        return low.Contains("согласен")
            || low.Contains("давай")
            || Regex.IsMatch(low, @"\bок(?:ей)?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            || low.Contains("погнали")
            || low.Contains("напиши чернов")
            || low.Contains("наброс")
            || low.Contains("черновик")
            || low.Contains("к этим задач")
            || low.Contains("для этих задач");
    }

    private static bool ProposalsSuggestAnchorOnboarding(IReadOnlyList<AiFoundryChatDraftProposalDto> proposals, string? concept)
    {
        if (proposals == null || proposals.Count == 0 || string.IsNullOrWhiteSpace(concept))
            return false;

        var explicitIndexes = proposals
            .Select((proposal, index) => new { proposal, index })
            .Where(x => ProposalUsesExplicitAnchor(x.proposal, concept))
            .Select(x => x.index)
            .ToList();
        if (explicitIndexes.Count == 0)
            return false;
        if (explicitIndexes[0] > Math.Min(1, proposals.Count - 1))
            return false;

        if (string.Equals(concept, "if", StringComparison.OrdinalIgnoreCase) && proposals.Count >= 3)
        {
            var hasElseBranch = proposals.Any(ProposalUsesElseBranch);
            if (!hasElseBranch)
                return false;
        }

        return true;
    }

    private static JsonObject BuildBlueprintRoutingDebugInfo(
        string actionName,
        AiFoundryChatMemoryDto memory,
        JsonObject args,
        IReadOnlyList<AiFoundryChatDraftProposalDto> proposals,
        IReadOnlyList<string>? issues = null,
        BlueprintRoutingDecision? routingDecision = null)
    {
        var decision = routingDecision ?? ResolveBlueprintRoutingDecision(memory, args, proposals, DetectAnchorConcept(memory));
        var diagnostics = decision.Diagnostics;
        var requestedCount = ExtractRequestedProposalCount(memory, args);
        var anchorLabel = AnchorConceptLabel(diagnostics.Concept);
        var explicitAnchorTitles = proposals
            .Where(x => ProposalUsesExplicitAnchor(x, diagnostics.Concept))
            .Select(x => x.Title ?? string.Empty)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Take(6)
            .ToList();

        static JsonArray ToJsonArray(IEnumerable<string> values)
        {
            var arr = new JsonArray();
            foreach (var value in values.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
                arr.Add(value);
            return arr;
        }

        return new JsonObject
        {
            ["kind"] = "blueprint-routing",
            ["actionName"] = actionName,
            ["anchorConcept"] = diagnostics.Concept,
            ["anchorLabel"] = anchorLabel,
            ["requestedCount"] = requestedCount,
            ["proposalCount"] = proposals.Count,
            ["routing"] = new JsonObject
            {
                ["mode"] = decision.EffectiveMode,
                ["rawMode"] = diagnostics.Mode,
                ["mentionsAnchor"] = diagnostics.MentionsAnchor,
                ["explicitStartLatest"] = diagnostics.LatestExplicitStart.Detected,
                ["explicitStartHaystack"] = diagnostics.HaystackExplicitStart.Detected,
                ["explicitStartLatestRegexMatched"] = diagnostics.LatestExplicitStart.RegexMatched,
                ["explicitStartHaystackRegexMatched"] = diagnostics.HaystackExplicitStart.RegexMatched,
                ["preAnchorDetected"] = diagnostics.PreAnchorDetected,
                ["preAnchorReason"] = diagnostics.PreAnchorReason,
                ["stepByStepSeries"] = diagnostics.StepByStepSeries,
                ["allowsExplicitOnboarding"] = decision.AllowsExplicitOnboarding,
                ["shouldAvoidExplicitAnchor"] = decision.ShouldAvoidExplicitAnchor,
                ["latestExplicitMarkers"] = ToJsonArray(diagnostics.LatestExplicitStart.MatchedMarkers),
                ["haystackExplicitMarkers"] = ToJsonArray(diagnostics.HaystackExplicitStart.MatchedMarkers),
                ["preAnchorMarkers"] = ToJsonArray(diagnostics.PreAnchorMarkers),
            },
            ["resolution"] = new JsonObject
            {
                ["hintMode"] = decision.HintMode,
                ["hintConcept"] = decision.HintConcept,
                ["hintAgreed"] = decision.HintAgreed,
                ["hintProposalEvidence"] = decision.HintProposalEvidence,
                ["inferredProposalOnboarding"] = decision.InferredProposalOnboarding,
                ["latestInstructionLooksLikeAssent"] = decision.LatestInstructionLooksLikeAssent,
                ["overrideToOnboarding"] = decision.OverrideToOnboarding,
                ["overrideReason"] = decision.OverrideReason,
            },
            ["memory"] = new JsonObject
            {
                ["latestExplicitInstruction"] = memory.LatestExplicitInstruction,
                ["latestTeachingScript"] = memory.LatestTeachingScript,
                ["recentGoalsTail"] = ToJsonArray(diagnostics.RecentGoalsTail),
                ["instructionHaystack"] = ShortenSingleLine(diagnostics.Haystack, 900),
            },
            ["proposals"] = new JsonObject
            {
                ["titles"] = ToJsonArray(proposals.Select(x => x.Title ?? string.Empty)),
                ["explicitAnchorTitles"] = ToJsonArray(explicitAnchorTitles),
            },
            ["validationIssues"] = ToJsonArray(issues ?? Array.Empty<string>()),
        };
    }

    private static void AttachToolResultDebugInfo(AiFoundryChatToolResultDto? result, string key, JsonNode? value)
    {
        if (result == null || string.IsNullOrWhiteSpace(key) || value == null)
            return;
        result.DebugInfo ??= new JsonObject();
        result.DebugInfo[key] = value;
    }

    private static bool RequestsTutorialLadder(AiFoundryChatMemoryDto memory)
    {
        var hay = BuildInstructionHaystack(memory).ToLowerInvariant();
        return RequestsTutorialLadder(hay);
    }

    private static bool RequestsTutorialLadder(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var hay = text.ToLowerInvariant();
        var asksTraining = hay.Contains("обучал")
            || hay.Contains("пошаг")
            || hay.Contains("лесенк")
            || hay.Contains("научат")
            || hay.Contains("маленьких программ")
            || hay.Contains("серия задач")
            || hay.Contains("серия программ")
            || hay.Contains("задачки")
            || hay.Contains("шаг за шаг");
        var asksSeveral = hay.Contains("задачи")
            || hay.Contains("задачк")
            || hay.Contains("несколько")
            || hay.Contains("5-")
            || hay.Contains("5–")
            || hay.Contains("6-")
            || hay.Contains("6–")
            || RequestsMorePrograms(hay);
        return asksTraining && asksSeveral;
    }

    private static bool ProposalLooksLikeFriendlyWalkthrough(AiFoundryChatDraftProposalDto proposal)
    {
        var hay = string.Join(" ", new[] { proposal.FullCondition, proposal.ConditionPreview, proposal.Title, proposal.Goal }.Where(x => !string.IsNullOrWhiteSpace(x)));
        if (string.IsNullOrWhiteSpace(hay))
            return false;
        return hay.Contains("Следуй шагам", StringComparison.OrdinalIgnoreCase)
            || hay.Contains("шаг 1", StringComparison.OrdinalIgnoreCase)
            || hay.Contains("Шаг 1", StringComparison.OrdinalIgnoreCase)
            || hay.Contains("Давай", StringComparison.OrdinalIgnoreCase)
            || hay.Contains("Сейчас", StringComparison.OrdinalIgnoreCase);
    }

    private static int? ExtractRequestedProposalCount(AiFoundryChatMemoryDto memory, JsonObject args)
    {
        var explicitCount = ReadInt(args, "count");
        if (explicitCount.HasValue && explicitCount.Value > 0)
            return Math.Clamp(explicitCount.Value, 1, 12);

        var latestOnly = string.Join(" ", new[]
        {
            memory.LatestExplicitInstruction,
            memory.LatestTeachingScript,
        }.Where(x => !string.IsNullOrWhiteSpace(x)));
        var latestCount = ExtractRequestedProposalCountFromText(latestOnly);
        if (latestCount.HasValue)
            return latestCount;

        var recentHay = string.Join(" ", memory.RecentGoals ?? new List<string>());
        var recentCount = ExtractRequestedProposalCountFromText(recentHay);
        if (recentCount.HasValue)
            return recentCount;

        if (RequestsTutorialLadder(memory))
            return 5;

        return null;
    }

    private static (Guid? AfterAssignmentId, string? AfterAssignmentTitle, string? HumanSummary) ResolveStrictRequestedPlacement(AiFoundryChatMemoryDto memory, JsonObject args)
    {
        var explicitAfterId = ReadGuid(args, "afterAssignmentId");
        if (explicitAfterId.HasValue)
        {
            var explicitTitle = ReadString(args, "afterAssignmentTitle") ?? ResolveRequestedAfterAssignmentTitle(memory, explicitAfterId);
            return (explicitAfterId, explicitTitle, $"после «{explicitTitle ?? "выбранного задания"}»");
        }

        var inspection = memory.LastCourseInspection;
        if (inspection == null || inspection.Assignments.Count == 0)
            return (null, null, null);

        var hay = string.Join(" ", new[]
        {
            memory.LatestExplicitInstruction,
            memory.LatestTeachingScript,
            string.Join(" ", memory.RecentGoals ?? new List<string>()),
        }.Where(x => !string.IsNullOrWhiteSpace(x))).ToLowerInvariant();

        var beforeMatch = Regex.Match(hay, @"перед\s+(?:задани(?:ем|я)?\s*)?(\d+(?:\.\d+)?)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (beforeMatch.Success)
        {
            var target = FindAssignmentByNumberToken(inspection, beforeMatch.Groups[1].Value);
            if (target != null)
            {
                var ordered = inspection.Assignments.OrderBy(x => x.Sort).ToList();
                var index = ordered.FindIndex(x => x.Id == target.Id);
                if (index > 0)
                {
                    var previous = ordered[index - 1];
                    return (previous.Id, previous.Title, $"между «{previous.Title}» и «{target.Title}»");
                }
            }
        }

        var afterMatch = Regex.Match(hay, @"после\s+(?:задани(?:я|ем)?\s*)?(\d+(?:\.\d+)?)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (afterMatch.Success)
        {
            var anchor = FindAssignmentByNumberToken(inspection, afterMatch.Groups[1].Value);
            if (anchor != null)
                return (anchor.Id, anchor.Title, $"после «{anchor.Title}»");
        }

        var requestedAnchorConcept = DetectAnchorConceptFromText(hay);
        if (QueryMentionsFirstAnchor(hay, requestedAnchorConcept))
        {
            var ordered = inspection.Assignments.OrderBy(x => x.Sort).ToList();
            var anchor = inspection.AroundAssignmentId.HasValue
                ? ordered.FirstOrDefault(x => x.Id == inspection.AroundAssignmentId.Value)
                : null;
            anchor ??= FindFirstExplicitAnchorAssignment(inspection, requestedAnchorConcept);
            if (anchor != null)
            {
                var index = ordered.FindIndex(x => x.Id == anchor.Id);
                if (index > 0)
                {
                    var previous = ordered[index - 1];
                    return (previous.Id, previous.Title, $"между «{previous.Title}» и «{anchor.Title}» (строго перед первым заданием с if)");
                }
            }
        }

        return (null, null, null);
    }

    private static bool RequestsFirstTaskStyleEvidence(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var hay = text.ToLowerInvariant();
        return hay.Contains("как первая задача")
            || hay.Contains("как первая")
            || hay.Contains("первая задача")
            || hay.Contains("эталон стиля")
            || hay.Contains("1 в 1")
            || hay.Contains("один в один");
    }

private static string? DetectAnchorConceptFromText(string? text)
{
    if (string.IsNullOrWhiteSpace(text))
        return null;

    var hay = text.ToLowerInvariant();
    foreach (var concept in new[] { "foreach", "switch", "while", "for", "if" })
    {
        if (Regex.IsMatch(hay, $@"(?<![A-Za-zА-Яа-я0-9_]){Regex.Escape(concept)}(?![A-Za-zА-Яа-я0-9_])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            return concept;
    }

    foreach (var pattern in new[]
    {
        @"(?:по|на)\s+теме\s+([^\n\r\.,;:!?]{2,80})",
        @"тему\s+([^\n\r\.,;:!?]{2,80})",
        @"освоени[ея]\s+([^\n\r\.,;:!?]{2,80})",
        @"использовать\s+([^\n\r\.,;:!?]{2,80})",
        @"пользоваться\s+([^\n\r\.,;:!?]{2,80})",
        @"работ[аы]\s+с\s+([^\n\r\.,;:!?]{2,80})",
        @"уч[иа]т[ья]?\s+([^\n\r\.,;:!?]{2,80})",
    })
    {
        var match = Regex.Match(hay, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success)
            continue;
        var candidate = match.Groups[1].Value.Trim().Trim('"', '\'', '«', '»');
        if (candidate.Length >= 2)
            return candidate;
    }

    if (hay.Contains("ветвлен") || hay.Contains("условн"))
        return "if";
    if (Regex.IsMatch(hay, @"(?<![A-Za-zА-Яа-я0-9_])case(?![A-Za-zА-Яа-я0-9_])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        return "switch";

    return null;
}

    private static string? DetectAnchorConcept(AiFoundryChatMemoryDto memory)
    => DetectAnchorConceptFromText(BuildInstructionHaystack(memory));

    private static string AnchorConceptLabel(string? concept)
    => string.IsNullOrWhiteSpace(concept) ? "новой конструкции" : concept.Trim().Trim('"', '\'', '«', '»');

    private static IEnumerable<string> AnchorConceptAliases(string? concept)
{
    concept = concept?.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(concept))
        yield break;

    yield return concept;
    if (string.Equals(concept, "if", StringComparison.OrdinalIgnoreCase))
    {
        yield return "if/else";
        yield return "else if";
        yield return "ветвлен";
        yield return "условн";
    }
    else if (string.Equals(concept, "switch", StringComparison.OrdinalIgnoreCase))
    {
        yield return "case";
        yield return "default";
    }
    else if (string.Equals(concept, "for", StringComparison.OrdinalIgnoreCase))
    {
        yield return "цикл for";
        yield return "итерац";
    }
    else if (string.Equals(concept, "while", StringComparison.OrdinalIgnoreCase))
    {
        yield return "цикл while";
        yield return "пока";
    }
}

    private static bool TextMentionsAnchorConcept(string? text, string? concept)
{
    if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(concept))
        return false;

    foreach (var alias in AnchorConceptAliases(concept))
    {
        if (Regex.IsMatch(text, $@"(?<![A-Za-zА-Яа-я0-9_]){Regex.Escape(alias)}(?![A-Za-zА-Яа-я0-9_])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            || text.Contains(alias, StringComparison.OrdinalIgnoreCase))
            return true;
    }

    return false;
}

    private static bool QueryMentionsFirstAnchor(string? text, string? concept)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(concept))
            return false;
        var hay = text.ToLowerInvariant();
        return hay.Contains($"первым {concept}")
            || hay.Contains($"первое {concept}")
            || hay.Contains($"первого {concept}")
            || hay.Contains($"первым появлением {concept}")
            || hay.Contains($"первое появление {concept}")
            || hay.Contains($"первого появления {concept}")
            || hay.Contains($"first {concept}")
            || Regex.IsMatch(hay, $@"перв\w*\s+.*\b{Regex.Escape(concept)}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static bool QueryMentionsFirstIfAnchor(string? text)
        => QueryMentionsFirstAnchor(text, "if");

    private static bool ContainsExplicitAnchorMarker(string? text, string? concept)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        concept = string.IsNullOrWhiteSpace(concept) ? DetectAnchorConceptFromText(text) : concept.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(concept))
            return false;

        return AnchorConceptAliases(concept).Any(alias =>
            Regex.IsMatch(text, $@"(?<![A-Za-zА-Яа-я0-9_]){Regex.Escape(alias)}(?![A-Za-zА-Яа-я0-9_])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            || text.Contains(alias, StringComparison.OrdinalIgnoreCase));
    }

    private static bool ContainsExplicitIfMarker(string? text)
        => ContainsExplicitAnchorMarker(text, "if");

    private static CourseAuditAssignmentSnapshot? FindFirstExplicitAnchorAssignment(IReadOnlyList<CourseAuditAssignmentSnapshot> ordered, string? concept)
    {
        if (ordered == null || ordered.Count == 0 || string.IsNullOrWhiteSpace(concept))
            return null;

        return ordered
            .OrderBy(x => x.Sort)
            .FirstOrDefault(x => ContainsExplicitAnchorMarker($"{x.Title}\n{ExtractPlainTextFromRichDescription(x.Description)}\n{x.AiOverview}", concept));
    }

    private static CourseAuditAssignmentSnapshot? FindFirstExplicitIfAssignment(IReadOnlyList<CourseAuditAssignmentSnapshot> ordered)
        => FindFirstExplicitAnchorAssignment(ordered, "if");

    private static AiFoundryCourseInspectionAssignmentDto? FindFirstExplicitAnchorAssignment(AiFoundryCourseInspectionDto inspection, string? concept)
    {
        if (inspection == null || inspection.Assignments.Count == 0 || string.IsNullOrWhiteSpace(concept))
            return null;

        return inspection.Assignments
            .OrderBy(x => x.Sort)
            .FirstOrDefault(x => ContainsExplicitAnchorMarker($"{x.Title}\n{x.DescriptionExcerpt}\n{x.AiOverview}", concept));
    }

    private static AiFoundryCourseInspectionAssignmentDto? FindFirstExplicitIfAssignment(AiFoundryCourseInspectionDto inspection)
        => FindFirstExplicitAnchorAssignment(inspection, "if");

    private static AiFoundryCourseInspectionAssignmentDto? FindAssignmentByNumberToken(AiFoundryCourseInspectionDto inspection, string numberToken)
    {
        if (inspection.Assignments.Count == 0 || string.IsNullOrWhiteSpace(numberToken))
            return null;

        var normalized = numberToken.Trim();
        return inspection.Assignments
            .OrderBy(x => x.Sort)
            .FirstOrDefault(x => Regex.IsMatch(x.Title ?? string.Empty, $@"\b{Regex.Escape(normalized)}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
    }

    private static bool RequiresFirstTaskStyleEvidence(AiFoundryChatMemoryDto memory)
    {
        var hay = string.Join(" ", new[]
        {
            memory.LatestExplicitInstruction,
            memory.LatestTeachingScript,
            string.Join(" ", memory.RecentGoals ?? new List<string>()),
        }.Where(x => !string.IsNullOrWhiteSpace(x))).ToLowerInvariant();
        return RequestsFirstTaskStyleEvidence(hay);
    }

    private static bool InspectionContainsFirstTask(AiFoundryCourseInspectionDto? inspection)
    {
        if (inspection == null)
            return false;
        return inspection.Assignments.Any(x => x.Sort <= 2 || Regex.IsMatch(x.Title ?? string.Empty, @"\bзадание\s*1(?!\d)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
    }

    private static bool InspectionContainsAssignment(AiFoundryCourseInspectionDto? inspection, Guid assignmentId)
    {
        if (inspection == null)
            return false;
        return inspection.Assignments.Any(x => x.Id == assignmentId);
    }

    private static string BuildInstructionHaystack(AiFoundryChatMemoryDto memory)
    {
        return string.Join(" ", new[]
        {
            memory.LatestExplicitInstruction,
            memory.LatestTeachingScript,
            string.Join(" ", memory.RecentGoals ?? new List<string>()),
        }.Where(x => !string.IsNullOrWhiteSpace(x)));
    }

    private static int? ExtractRequestedProposalCountFromText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var hay = text.ToLowerInvariant();
        var match = Regex.Match(hay, @"\b(\d{1,2})\s*(?:задач|обучал|мостик|вариант|программ)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (match.Success && int.TryParse(match.Groups[1].Value, out var numeric))
            return Math.Clamp(numeric, 1, 12);

        if (hay.Contains("пять задач") || hay.Contains("5 задач") || hay.Contains("5 обучал") || hay.Contains("5 программ"))
            return 5;
        if (hay.Contains("шесть задач") || hay.Contains("6 задач") || hay.Contains("6 программ"))
            return 6;
        if (hay.Contains("семь задач") || hay.Contains("7 задач") || hay.Contains("7 программ"))
            return 7;
        if (RequestsMorePrograms(hay))
            return 6;

        return null;
    }

    private static bool RequestsMorePrograms(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var hay = text.ToLowerInvariant();
        return hay.Contains("больше программ")
            || hay.Contains("побольше программ")
            || hay.Contains("таких программ было больше")
            || hay.Contains("желательно чтобы таких программ было больше")
            || hay.Contains("несколько программ")
            || hay.Contains("серия программ");
    }

    private static bool RequestsMorePrograms(AiFoundryChatMemoryDto memory)
        => RequestsMorePrograms(BuildInstructionHaystack(memory));

    private static AnchorStartDetection DetectExplicitAnchorFromStartText(string? text, string? concept = null)
    {
        var low = (text ?? string.Empty).ToLowerInvariant();
        concept = (concept ?? DetectAnchorConceptFromText(low))?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(low) || string.IsNullOrWhiteSpace(concept))
            return new AnchorStartDetection();

        var escaped = Regex.Escape(concept);
        var markers = new[]
        {
            $"на сам {concept}",
            $"именно {concept}",
            $"уже с {concept}",
            $"сразу {concept}",
            $"первый шаг {concept}",
            $"первый шаг — {concept}",
            $"первый шаг - {concept}",
            $"первым должен быть {concept}",
            $"можно {concept}",
            $"разрешаю {concept}",
            $"научат пользоваться {concept}",
            $"научат работать с {concept}",
            $"обучалки по {concept}",
            $"задачи по {concept}",
        };
        var matched = markers.Where(low.Contains).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var regexMatched = Regex.IsMatch(low, $@"сначала\s+(?:просто\s+)?{escaped}(?![A-Za-zА-Яа-я0-9_])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            || Regex.IsMatch(low, $@"первый\s+шаг[^\n]{{0,40}}(?<![A-Za-zА-Яа-я0-9_]){escaped}(?![A-Za-zА-Яа-я0-9_])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return new AnchorStartDetection
        {
            Detected = matched.Count > 0 || regexMatched,
            RegexMatched = regexMatched,
            MatchedMarkers = matched,
        };
    }

    private static AnchorRoutingDiagnostics AnalyzeAnchorRouting(AiFoundryChatMemoryDto memory, string? concept = null, string? prompt = null, string? sourceText = null)
    {
        concept ??= DetectAnchorConcept(memory) ?? DetectAnchorConceptFromText(prompt) ?? DetectAnchorConceptFromText(sourceText);
        concept = (concept ?? string.Empty).Trim().ToLowerInvariant();
        var haystack = BuildInstructionHaystack(memory);
        var latestText = string.Join(" ", new[] { memory.LatestExplicitInstruction, memory.LatestTeachingScript }.Where(x => !string.IsNullOrWhiteSpace(x)));
        var recentGoalsTail = (memory.RecentGoals ?? new List<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).TakeLast(4).ToList();
        var stepByStepSeries = RequestsStepByStepSeries(memory, prompt, sourceText);
        if (string.IsNullOrWhiteSpace(concept))
        {
            return new AnchorRoutingDiagnostics
            {
                Haystack = haystack,
                LatestText = latestText,
                LatestTeachingScript = memory.LatestTeachingScript ?? string.Empty,
                RecentGoalsTail = recentGoalsTail,
                StepByStepSeries = stepByStepSeries,
            };
        }

        var latestExplicit = DetectExplicitAnchorFromStartText(latestText, concept);
        var haystackExplicit = DetectExplicitAnchorFromStartText(haystack, concept);
        var mentionsAnchor = TextMentionsAnchorConcept(haystack, concept);
        var anchorLearningSeries = RequestsAnchorLearningSeries(haystack, concept) || RequestsAnchorLearningSeries(latestText, concept);
        var preAnchorDetected = false;
        var preAnchorReason = "none";
        var preMarkers = new List<string>();

        if (!latestExplicit.Detected && !haystackExplicit.Detected && mentionsAnchor && !anchorLearningSeries)
        {
            var low = haystack.ToLowerInvariant();
            var explicitBeforeMarkers = new List<string>
            {
                $"перед первым {concept}",
                $"перед первым появлением {concept}",
                $"до первого {concept}",
                $"до темы {concept}",
                $"до {concept}",
                $"перед темой {concept}",
                $"без самого {concept}",
                $"без {concept} в условиях",
                $"в задачках до {concept} не может быть {concept}",
                $"в задачах до {concept} не может быть {concept}",
                $"прежде чем вводить {concept}",
                $"до того как вводить {concept}",
            };
            if (string.Equals(concept, "if", StringComparison.OrdinalIgnoreCase))
                explicitBeforeMarkers.AddRange(new[] { "до ветвлен", "до условн" });
            preMarkers = explicitBeforeMarkers.Where(low.Contains).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (preMarkers.Count > 0)
            {
                preAnchorDetected = true;
                preAnchorReason = "explicit-before-marker";
            }
            else
            {
                var mentionsCourse = low.Contains("курс") || low.Contains("задан") || low.Contains("assignment");
                var abruptMatches = new[] { "без введен", "без обучал", "без объяснен", "резко", "слишком рано", "появля" }.Where(low.Contains).ToList();
                var prepMatches = new[] { "подводящ", "подготов", "обучал", "лесенк", "пошаг", "перед темой" }.Where(low.Contains).ToList();
                if (mentionsCourse && abruptMatches.Count > 0 && prepMatches.Count > 0)
                {
                    preAnchorDetected = true;
                    preAnchorReason = "abrupt-course-gap-fallback";
                    preMarkers = new[] { mentionsCourse ? "курс/задания" : null }
                        .Concat(abruptMatches)
                        .Concat(prepMatches)
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList()!;
                }
            }
        }

        var allowsExplicitOnboarding = !preAnchorDetected && ((stepByStepSeries && mentionsAnchor) || anchorLearningSeries);
        return new AnchorRoutingDiagnostics
        {
            Concept = concept,
            Haystack = haystack,
            LatestText = latestText,
            LatestTeachingScript = memory.LatestTeachingScript ?? string.Empty,
            RecentGoalsTail = recentGoalsTail,
            MentionsAnchor = mentionsAnchor,
            LatestExplicitStart = latestExplicit,
            HaystackExplicitStart = haystackExplicit,
            PreAnchorDetected = preAnchorDetected,
            PreAnchorReason = preAnchorReason,
            PreAnchorMarkers = preMarkers,
            StepByStepSeries = stepByStepSeries,
            AllowsExplicitOnboarding = allowsExplicitOnboarding,
            Mode = preAnchorDetected ? "pre-anchor" : (allowsExplicitOnboarding ? "anchor-onboarding" : "neutral"),
        };
    }

    private static bool RequestsAnchorLearningSeries(string? text, string? concept)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(concept))
            return false;
        var hay = text.ToLowerInvariant();
        if (!TextMentionsAnchorConcept(hay, concept))
            return false;
        var onboardingMarkers = hay.Contains("обучал")
            || hay.Contains("научат")
            || hay.Contains("пользоваться")
            || hay.Contains("работать с")
            || hay.Contains("перед этим")
            || hay.Contains("до этого")
            || hay.Contains("перед тем")
            || hay.Contains("подготов")
            || hay.Contains("пошаг")
            || hay.Contains("лесенк");
        return onboardingMarkers;
    }

    private static bool RequestsExplicitAnchorFromStart(AiFoundryChatMemoryDto memory, string? concept = null)
    {
        var diagnostics = AnalyzeAnchorRouting(memory, concept);
        return diagnostics.LatestExplicitStart.Detected || diagnostics.HaystackExplicitStart.Detected;
    }

    private static bool RequestsExplicitConceptFromStart(AiFoundryChatMemoryDto memory)
        => RequestsExplicitAnchorFromStart(memory, "if");

    private static bool RequestsPreAnchorScaffolding(AiFoundryChatMemoryDto memory, string? concept = null)
        => AnalyzeAnchorRouting(memory, concept).PreAnchorDetected;

    private static bool RequestsPreConceptScaffolding(AiFoundryChatMemoryDto memory)
        => RequestsPreAnchorScaffolding(memory, "if");

    private static bool ShouldAvoidExplicitAnchorBeforeAnchor(AiFoundryChatMemoryDto memory, string? concept = null)
    {
        var diagnostics = AnalyzeAnchorRouting(memory, concept);
        if (string.IsNullOrWhiteSpace(diagnostics.Concept))
            return false;
        if (diagnostics.PreAnchorDetected)
            return true;

        var low = diagnostics.Haystack.ToLowerInvariant();
        var bridgeBefore = low.Contains("перед") || low.Contains("до") || low.Contains("обучал");
        var explicitFromStart = diagnostics.LatestExplicitStart.Detected || diagnostics.HaystackExplicitStart.Detected;
        return diagnostics.MentionsAnchor && bridgeBefore && !explicitFromStart;
    }

    private static bool ShouldAvoidExplicitConceptBeforeAnchor(AiFoundryChatMemoryDto memory)
        => ShouldAvoidExplicitAnchorBeforeAnchor(memory, "if");

    private static bool RequestsStepByStepSeries(AiFoundryChatMemoryDto memory, string? prompt = null, string? sourceText = null)
    {
        var scenario = AiGenerationScenarioRouter.Resolve(memory, prompt, sourceText, 5);
        return scenario.Id is "guided-onboarding-ladder" or "step-by-step-ladder" or "micro-program-series";
    }

    private static bool AllowsExplicitAnchorOnboarding(AiFoundryChatMemoryDto memory, string? prompt = null, string? sourceText = null, string? concept = null)
        => AnalyzeAnchorRouting(memory, concept, prompt, sourceText).AllowsExplicitOnboarding;

    private static bool AllowsExplicitConceptOnboarding(AiFoundryChatMemoryDto memory, string? prompt = null, string? sourceText = null)
        => AllowsExplicitAnchorOnboarding(memory, prompt, sourceText, "if");

    private static string RewritePromptForStepByStepSeries(string prompt, AiFoundryChatMemoryDto memory, int count)
        => AiGenerationScenarioPromptAdapter.RewritePrompt(prompt, memory, count);

    private static string RewriteSourceTextForStepByStepSeries(string? sourceText, AiFoundryChatMemoryDto memory, int count)
        => AiGenerationScenarioPromptAdapter.RewriteSourceText(sourceText, memory, count);

    private static string DetermineDirectGenerationMode(AiFoundryChatMemoryDto memory, string prompt, string? sourceText, int requestedCount)
        => AiGenerationScenarioPromptAdapter.DetermineMode(memory, prompt, sourceText, requestedCount);

    private static bool ProposalUsesExplicitAnchor(AiFoundryChatDraftProposalDto proposal, string? concept)
    {
        var hay = string.Join(" ", new[] { proposal.Title, proposal.ConditionPreview, proposal.FullCondition, proposal.Goal }.Where(x => !string.IsNullOrWhiteSpace(x)));
        return ContainsExplicitAnchorMarker(hay, concept);
    }

    private static bool ProposalUsesExplicitConcept(AiFoundryChatDraftProposalDto proposal)
        => ProposalUsesExplicitAnchor(proposal, "if");

    private static bool ProposalUsesElseBranch(AiFoundryChatDraftProposalDto proposal)
    {
        var hay = string.Join(" ", new[] { proposal.Title, proposal.ConditionPreview, proposal.FullCondition, proposal.Goal }.Where(x => !string.IsNullOrWhiteSpace(x)));
        return Regex.IsMatch(hay, @"\belse\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            || hay.Contains("иначе", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ProposalLooksLikeSmallProgram(AiFoundryChatDraftProposalDto proposal, string? concept = null)
    {
        var hay = string.Join(" ", new[] { proposal.Title, proposal.ConditionPreview, proposal.FullCondition, proposal.Goal }.Where(x => !string.IsNullOrWhiteSpace(x))).ToLowerInvariant();
        return ProposalUsesExplicitAnchor(proposal, concept ?? DetectAnchorConceptFromText(hay))
            || hay.Contains("программ")
            || hay.Contains("код")
            || hay.Contains("cout")
            || hay.Contains("cin")
            || hay.Contains("введ")
            || hay.Contains("вывед")
            || hay.Contains("на экран")
            || hay.Contains("условие");
    }

    private static bool ProposalLooksTooAbstractForAnchorOnboarding(AiFoundryChatDraftProposalDto proposal, string? concept)
    {
        if (string.IsNullOrWhiteSpace(concept))
            return false;

        var hay = string.Join(" ", new[] { proposal.Title, proposal.ConditionPreview, proposal.FullCondition, proposal.Goal }.Where(x => !string.IsNullOrWhiteSpace(x))).ToLowerInvariant();
        var abstractTopic = hay.Contains("сравнен")
            || hay.Contains("логичес")
            || hay.Contains("остат")
            || hay.Contains("делим")
            || hay.Contains("булев")
            || hay.Contains("выражен")
            || hay.Contains("подготов")
            || hay.Contains("теори")
            || hay.Contains("абстрак");
        return abstractTopic && !ProposalLooksLikeSmallProgram(proposal, concept) && !ProposalUsesExplicitAnchor(proposal, concept);
    }

    private static bool ProposalLooksTooAbstractForConceptOnboarding(AiFoundryChatDraftProposalDto proposal)
        => ProposalLooksTooAbstractForAnchorOnboarding(proposal, "if");

    private static bool ProposalUsesDryOlympiadTone(AiFoundryChatDraftProposalDto proposal)
    {
        var hay = (proposal.FullCondition ?? proposal.ConditionPreview ?? proposal.Title ?? string.Empty).Trim();
        return Regex.IsMatch(hay, @"^(?:напишите программу|вам нужно|требуется|даны|вход|выход)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            || hay.Contains("Вход", StringComparison.OrdinalIgnoreCase)
            || hay.Contains("Выход", StringComparison.OrdinalIgnoreCase);
    }

    private static List<AiFoundryChatDraftProposalDto> ReadChatBlueprintProposals(JsonObject args, AiFoundryChatDraftBlueprintDto? previous = null)
    {
        var result = new List<AiFoundryChatDraftProposalDto>();
        if (args["proposals"] is not JsonArray proposals)
            return result;
        for (var index = 0; index < proposals.Count; index++)
        {
            if (proposals[index] is not JsonObject obj)
                continue;
            var title = obj["title"]?.ToString()?.Trim();
            var conditionPreview = obj["conditionPreview"]?.ToString()?.Trim() ?? obj["summary"]?.ToString()?.Trim();
            var fullCondition = obj["fullCondition"]?.ToString()?.Trim() ?? conditionPreview;
            if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(conditionPreview) && string.IsNullOrWhiteSpace(fullCondition))
                continue;

            var hasExplicitId = Guid.TryParse(obj["id"]?.ToString(), out var parsedId);
            var existing = hasExplicitId
                ? previous?.Proposals.FirstOrDefault(x => x.Id == parsedId)
                : (previous != null && index < previous.Proposals.Count ? previous.Proposals[index] : null);

            var proposal = new AiFoundryChatDraftProposalDto
            {
                Id = hasExplicitId ? parsedId : existing?.Id ?? Guid.NewGuid(),
                Title = string.IsNullOrWhiteSpace(title) ? ShortenSingleLine(conditionPreview ?? fullCondition ?? existing?.Title ?? "Новая задача", 80) : title,
                AssignmentType = string.IsNullOrWhiteSpace(obj["assignmentType"]?.ToString()) ? (existing?.AssignmentType ?? "code-test") : obj["assignmentType"]!.ToString()!.Trim(),
                Difficulty = Math.Clamp(int.TryParse(obj["difficulty"]?.ToString(), out var difficulty) ? difficulty : existing?.Difficulty ?? 2, 1, 5),
                Goal = ShortenMultiline(obj["goal"]?.ToString() ?? obj["microGoal"]?.ToString() ?? existing?.Goal ?? string.Empty, 400),
                ConditionPreview = ShortenMultiline(conditionPreview ?? existing?.ConditionPreview ?? fullCondition ?? string.Empty, 900),
                FullCondition = ShortenMultiline(fullCondition ?? existing?.FullCondition ?? conditionPreview ?? string.Empty, 4000),
                PlacementAfterAssignmentId = Guid.TryParse(obj["placementAfterAssignmentId"]?.ToString() ?? obj["afterAssignmentId"]?.ToString(), out var parsedAfterId) ? parsedAfterId : existing?.PlacementAfterAssignmentId,
                PlacementAfterTitle = ShortenSingleLine(obj["placementAfterTitle"]?.ToString() ?? obj["afterAssignmentTitle"]?.ToString() ?? existing?.PlacementAfterTitle ?? string.Empty, 180),
                PlacementReason = ShortenSingleLine(obj["placementReason"]?.ToString() ?? existing?.PlacementReason ?? string.Empty, 240),
                Status = string.IsNullOrWhiteSpace(obj["status"]?.ToString()) ? (existing?.Status ?? "draft") : obj["status"]!.ToString()!.Trim(),
            };
            proposal.MustKeep = obj.ContainsKey("mustKeep") ? ReadStringList(obj, "mustKeep") : CloneStringList(existing?.MustKeep);
            proposal.Avoid = obj.ContainsKey("avoid") ? ReadStringList(obj, "avoid") : CloneStringList(existing?.Avoid);
            proposal.PublicTests = obj.ContainsKey("publicTests") ? ReadChatDraftTests(obj, "publicTests") : CloneChatDraftTests(existing?.PublicTests);
            proposal.HiddenTests = obj.ContainsKey("hiddenTests") ? ReadChatDraftTests(obj, "hiddenTests") : CloneChatDraftTests(existing?.HiddenTests);
            result.Add(proposal);
        }
        return result;
    }

    private static List<string> ReadStringList(JsonObject obj, string propertyName)
    {
        var result = new List<string>();
        if (obj[propertyName] is not JsonArray arr)
            return result;
        foreach (var node in arr)
        {
            var value = node?.ToString()?.Trim();
            if (!string.IsNullOrWhiteSpace(value) && !result.Contains(value, StringComparer.OrdinalIgnoreCase))
                result.Add(ShortenSingleLine(value, 240));
        }
        return result;
    }

    private static List<string> CloneStringList(IEnumerable<string>? source)
        => source?.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => ShortenSingleLine(x, 240)).Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? new List<string>();


    private static List<AiFoundryChatDraftTestPreviewDto> ReadChatDraftTests(JsonObject obj, string propertyName)
    {
        var result = new List<AiFoundryChatDraftTestPreviewDto>();
        if (obj[propertyName] is not JsonArray arr)
            return result;
        foreach (var node in arr)
        {
            if (node is not JsonObject testObj)
                continue;
            var input = testObj["input"]?.ToString() ?? string.Empty;
            var expectedOutput = testObj["expectedOutput"]?.ToString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(input) && string.IsNullOrWhiteSpace(expectedOutput))
                continue;
            result.Add(new AiFoundryChatDraftTestPreviewDto
            {
                Input = ShortenMultiline(input, 200),
                ExpectedOutput = ShortenMultiline(expectedOutput, 200),
            });
        }
        return result;
    }

    private static List<AiFoundryChatDraftTestPreviewDto> CloneChatDraftTests(IEnumerable<AiFoundryChatDraftTestPreviewDto>? source)
        => source?.Select(x => new AiFoundryChatDraftTestPreviewDto
        {
            Input = ShortenMultiline(x.Input, 200),
            ExpectedOutput = ShortenMultiline(x.ExpectedOutput, 200),
        }).ToList() ?? new List<AiFoundryChatDraftTestPreviewDto>();

    private static string BuildChatBlueprintSummaryText(List<AiFoundryChatDraftProposalDto> proposals)
    {
        if (proposals.Count == 0)
            return "Сохранила пустой набор примерных условий.";
        if (proposals.Count == 1)
        {
            var proposal = proposals[0];
            var preview = !string.IsNullOrWhiteSpace(proposal.ConditionPreview)
                ? ShortenSingleLine(proposal.ConditionPreview, 120)
                : ShortenSingleLine(proposal.FullCondition ?? string.Empty, 120);
            return string.IsNullOrWhiteSpace(preview)
                ? $"Сохранила 1 примерное условие: {proposal.Title}."
                : $"Сохранила 1 примерное условие: {proposal.Title}. Черновик: {preview}";
        }
        return $"Сохранила {proposals.Count} примерных условий для обсуждения и правок.";
    }

    private static string BuildChatBlueprintSummary(AiFoundryChatDraftBlueprintDto blueprint, AiFoundryChatMemoryDto? memory = null)
    {
        if (blueprint == null || blueprint.Proposals.Count == 0)
            return "Примерных условий пока нет.";
        var sb = new StringBuilder();
        sb.AppendLine($"Черновой набор условий из чата · revision {Math.Max(1, blueprint.Revision)}");
        if (!string.IsNullOrWhiteSpace(blueprint.Summary))
            sb.AppendLine(ShortenMultiline(blueprint.Summary, 260));
        for (var i = 0; i < blueprint.Proposals.Count; i++)
        {
            var proposal = blueprint.Proposals[i];
            sb.AppendLine();
            sb.AppendLine($"Вариант {i + 1}. {proposal.Title}");
            var previewText = !string.IsNullOrWhiteSpace(proposal.FullCondition) ? proposal.FullCondition : proposal.ConditionPreview;
            var looksFriendlyWalkthrough = !string.IsNullOrWhiteSpace(previewText)
                && previewText.Contains("Следуй шагам", StringComparison.OrdinalIgnoreCase);
            if (!looksFriendlyWalkthrough)
                sb.AppendLine($"Тип: {proposal.AssignmentType} · сложность {proposal.Difficulty}/5 · статус {proposal.Status}");
            if (!string.IsNullOrWhiteSpace(proposal.Goal))
                sb.AppendLine($"Цель: {ShortenSingleLine(proposal.Goal, 220)}");
            if (proposal.PlacementAfterAssignmentId.HasValue || !string.IsNullOrWhiteSpace(proposal.PlacementAfterTitle))
                sb.AppendLine($"Позиция: после «{proposal.PlacementAfterTitle ?? "выбранного задания"}»{(proposal.PlacementAfterAssignmentId.HasValue ? $" [{proposal.PlacementAfterAssignmentId}]" : string.Empty)}");
            if (!string.IsNullOrWhiteSpace(proposal.PlacementReason))
                sb.AppendLine($"Почему сюда: {ShortenSingleLine(proposal.PlacementReason, 220)}");

            if (!string.IsNullOrWhiteSpace(previewText))
            {
                sb.AppendLine(looksFriendlyWalkthrough ? "Текст задания:" : "Черновик условия:");
                sb.AppendLine(ShortenMultiline(previewText, 1200));
            }

            if (proposal.MustKeep.Count > 0)
                sb.AppendLine($"Сохранить обязательно: {string.Join(", ", proposal.MustKeep.Take(6))}");
            if (proposal.Avoid.Count > 0)
                sb.AppendLine($"Не добавлять: {string.Join(", ", proposal.Avoid.Take(6))}");
            if (proposal.PublicTests.Count > 0)
            {
                sb.AppendLine("Публичные тесты:");
                foreach (var test in proposal.PublicTests.Take(6))
                    sb.AppendLine($"- input: {test.Input} | expected: {test.ExpectedOutput}");
            }
            if (proposal.HiddenTests.Count > 0)
            {
                sb.AppendLine("Скрытые тесты:");
                foreach (var test in proposal.HiddenTests.Take(6))
                    sb.AppendLine($"- input: {test.Input} | expected: {test.ExpectedOutput}");
            }
        }
        sb.AppendLine();
        if (memory?.PreferAutonomousCompletion == true || IsDirectFinalResultIntent(memory?.LatestExplicitInstruction))
            sb.AppendLine("Если самопроверка находит проблему — исправь её сразу. Если проблем нет, переходи к следующему шагу без отдельного UX-одобрения.");
        else
            sb.AppendLine("Напиши, что менять. Когда всё ок, скажи: 'одобряю, закидывай в черновик'.");
        return sb.ToString().Trim();
    }

    private static string BuildPromptFromChatBlueprintProposal(AiFoundryChatDraftProposalDto proposal, AiFoundryChatMemoryDto memory)
    {
        var sb = new StringBuilder();
        var latestInstruction = string.Join(" ", new[] { memory.LatestExplicitInstruction, memory.LatestTeachingScript }.Where(x => !string.IsNullOrWhiteSpace(x)));
        var latestInstructionLower = latestInstruction.ToLowerInvariant();
        var exactStyleRequested = latestInstructionLower.Contains("1 в 1")
            || latestInstructionLower.Contains("один в один")
            || latestInstructionLower.Contains("как первое задание")
            || latestInstructionLower.Contains("как первая задача")
            || latestInstructionLower.Contains("в стиле первого задания")
            || latestInstructionLower.Contains("стиль первого задания")
            || latestInstructionLower.Contains("повтори стиль");

        AiFoundryCourseInspectionAssignmentDto? exemplar = null;
        if (memory.LastCourseInspection?.Assignments != null && memory.LastCourseInspection.Assignments.Count > 0)
        {
            exemplar = memory.LastCourseInspection.Assignments
                .OrderBy(x => x.Sort)
                .FirstOrDefault(x =>
                    x.Title.Contains("Задание 1", StringComparison.OrdinalIgnoreCase)
                    || x.Title.Contains("Твой первый вывод", StringComparison.OrdinalIgnoreCase)
                    || x.Sort <= 1)
                ?? memory.LastCourseInspection.Assignments.OrderBy(x => x.Sort).FirstOrDefault();
        }

        sb.AppendLine("Нужно превратить уже согласованное примерное условие из чата в полноценный AI draft без смены учебной мысли.");
        if (!string.IsNullOrWhiteSpace(proposal.Goal))
            sb.AppendLine($"Цель: {proposal.Goal}");
        sb.AppendLine($"Название-ориентир: {proposal.Title}");
        if (proposal.MustKeep.Count > 0)
            sb.AppendLine($"Обязательные элементы: {string.Join(", ", proposal.MustKeep)}");
        if (proposal.Avoid.Count > 0)
            sb.AppendLine($"Запрещено добавлять: {string.Join(", ", proposal.Avoid)}");
        if (!string.IsNullOrWhiteSpace(memory.LatestTeachingScript))
            sb.AppendLine("Следуй teaching-script из чата максимально близко. Не теряй порядок шагов, если он был явно задан.");
        if (exactStyleRequested)
        {
            sb.AppendLine("Пользователь просит повторить стиль почти 1-в-1. Не усредняй стиль по курсу и не перепридумывай подачу.");
            sb.AppendLine("Если в эталоне есть дружелюбное вступление, формат «Следуй шагам», короткие команды и пояснения в скобках — сохрани тот же scaffold и уровень подробности.");
            if (exemplar != null)
            {
                sb.AppendLine($"Стилевой эталон в курсе: «{exemplar.Title}» (sort {exemplar.Sort}). Используй именно его как главный ориентир по тону, структуре и порядку шагов.");
                if (!string.IsNullOrWhiteSpace(exemplar.DescriptionExcerpt))
                    sb.AppendLine($"Короткая выжимка эталона: {ShortenSingleLine(exemplar.DescriptionExcerpt, 220)}");
            }
        }
        if (proposal.PlacementAfterAssignmentId.HasValue || !string.IsNullOrWhiteSpace(proposal.PlacementAfterTitle))
            sb.AppendLine($"Позиция в курсе должна быть после задания «{proposal.PlacementAfterTitle ?? "выбранный anchor"}»{(proposal.PlacementAfterAssignmentId.HasValue ? $" [{proposal.PlacementAfterAssignmentId}]" : string.Empty)}.");
        if (!string.IsNullOrWhiteSpace(proposal.PlacementReason))
            sb.AppendLine($"Обоснование позиции: {proposal.PlacementReason}");
        if (!string.IsNullOrWhiteSpace(proposal.FullCondition))
            sb.AppendLine("Одобренный черновик условия из чата — это канонический источник истины. Финальный draft обязан сохранять ту же учебную мысль, те же литералы, те же шаги и тот же scope.");
        sb.AppendLine("Если в согласованном условии уже есть конкретные строки кода, точный вывод, точные ограничения или конкретный каркас программы, не подменяй их новыми значениями.");
        sb.AppendLine("Сначала сделай хорошее итоговое условие, тесты и reference solution. Не уезжай в новую тему и не расширяй scope.");
        return sb.ToString().Trim();
    }

    private static string BuildStructuredContextFromChatBlueprintProposal(AiFoundryChatDraftProposalDto proposal, AiFoundryChatDraftBlueprintDto blueprint, Guid sessionId)
    {
        return JsonSerializer.Serialize(new
        {
            kind = "approved-chat-blueprint",
            sessionId,
            revision = Math.Max(1, blueprint?.Revision ?? 1),
            title = proposal.Title,
            assignmentType = proposal.AssignmentType,
            difficulty = proposal.Difficulty,
            goal = proposal.Goal,
            conditionPreview = proposal.ConditionPreview,
            fullCondition = proposal.FullCondition,
            mustKeep = proposal.MustKeep,
            avoid = proposal.Avoid,
            placementAfterAssignmentId = proposal.PlacementAfterAssignmentId,
            placementAfterTitle = proposal.PlacementAfterTitle,
            placementReason = proposal.PlacementReason,
            publicTests = proposal.PublicTests.Select(x => new { input = x.Input, expectedOutput = x.ExpectedOutput }).ToList(),
            hiddenTests = proposal.HiddenTests.Select(x => new { input = x.Input, expectedOutput = x.ExpectedOutput }).ToList(),
        }, JsonOptions);
    }

    private static string BuildSourceTextFromChatBlueprintProposal(AiFoundryChatDraftProposalDto proposal, AiFoundryChatMemoryDto memory)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Название: {proposal.Title}");
        if (!string.IsNullOrWhiteSpace(proposal.Goal))
            sb.AppendLine($"Учебная цель: {proposal.Goal}");
        if (!string.IsNullOrWhiteSpace(proposal.FullCondition))
        {
            sb.AppendLine("Примерное условие:");
            sb.AppendLine(proposal.FullCondition);
        }
        else if (!string.IsNullOrWhiteSpace(proposal.ConditionPreview))
        {
            sb.AppendLine("Примерное условие:");
            sb.AppendLine(proposal.ConditionPreview);
        }
        if (proposal.PlacementAfterAssignmentId.HasValue || !string.IsNullOrWhiteSpace(proposal.PlacementAfterTitle))
        {
            sb.AppendLine($"Позиция в курсе: после «{proposal.PlacementAfterTitle ?? "выбранного задания"}»{(proposal.PlacementAfterAssignmentId.HasValue ? $" [{proposal.PlacementAfterAssignmentId}]" : string.Empty)}");
            if (!string.IsNullOrWhiteSpace(proposal.PlacementReason))
                sb.AppendLine($"Почему именно туда: {proposal.PlacementReason}");
        }
        if (!string.IsNullOrWhiteSpace(memory.LatestExplicitInstruction))
        {
            sb.AppendLine();
            sb.AppendLine("Последняя явная инструкция пользователя:");
            sb.AppendLine(memory.LatestExplicitInstruction);
        }
        if (!string.IsNullOrWhiteSpace(memory.LatestTeachingScript))
        {
            sb.AppendLine();
            sb.AppendLine("Teaching-script пользователя:");
            sb.AppendLine(memory.LatestTeachingScript);
        }
        sb.AppendLine();
        sb.AppendLine("Важно: это уже одобренный blueprint из чата. Нельзя менять тему, ключевые литералы, итоговый вывод и порядок шагов без прямого запроса пользователя.");
        if (proposal.MustKeep.Count > 0)
            sb.AppendLine($"Сохранить обязательно: {string.Join(", ", proposal.MustKeep)}");
        if (proposal.Avoid.Count > 0)
            sb.AppendLine($"Не добавлять: {string.Join(", ", proposal.Avoid)}");
        if (proposal.PublicTests.Count > 0)
        {
            sb.AppendLine("Примерные публичные тесты:");
            foreach (var test in proposal.PublicTests.Take(6))
                sb.AppendLine($"- input: {test.Input} | expected: {test.ExpectedOutput}");
        }
        if (proposal.HiddenTests.Count > 0)
        {
            sb.AppendLine("Примерные скрытые тесты:");
            foreach (var test in proposal.HiddenTests.Take(6))
                sb.AppendLine($"- input: {test.Input} | expected: {test.ExpectedOutput}");
        }
        return sb.ToString().Trim();
    }

    private static Guid? ResolveRequestedAfterAssignmentId(JsonObject args, AiFoundryChatMemoryDto memory)
    {
        var requested = ReadGuid(args, "afterAssignmentId");
        if (requested.HasValue)
            return requested.Value;
        if (memory.AgentState?.PlacementAfterAssignmentId.HasValue == true)
            return memory.AgentState.PlacementAfterAssignmentId.Value;

        var fromCandidates = memory.AgentState?.PlacementCandidates?.FirstOrDefault(x => x.AfterAssignmentId.HasValue)?.AfterAssignmentId;
        if (fromCandidates.HasValue)
            return fromCandidates.Value;

        return BuildInspectionPlacementCandidate(memory.LastCourseInspection)?.AfterAssignmentId;
    }

    private static string? ResolveRequestedAfterAssignmentTitle(AiFoundryChatMemoryDto memory, Guid? afterAssignmentId)
    {
        if (!afterAssignmentId.HasValue)
            return memory.AgentState?.PlacementAfterAssignmentTitle
                ?? BuildInspectionPlacementCandidate(memory.LastCourseInspection)?.AfterAssignmentTitle;
        if (memory.AgentState?.PlacementAfterAssignmentId == afterAssignmentId)
            return memory.AgentState.PlacementAfterAssignmentTitle;

        var fromCandidates = memory.AgentState?.PlacementCandidates?
            .FirstOrDefault(x => x.AfterAssignmentId == afterAssignmentId)?.AfterAssignmentTitle;
        if (!string.IsNullOrWhiteSpace(fromCandidates))
            return fromCandidates;

        var inspectionFallback = BuildInspectionPlacementCandidate(memory.LastCourseInspection);
        return inspectionFallback?.AfterAssignmentId == afterAssignmentId ? inspectionFallback.AfterAssignmentTitle : null;
    }

    private static AiFoundryAgentPlacementCandidateDto? BuildInspectionPlacementCandidate(AiFoundryCourseInspectionDto? inspection)
    {
        if (inspection == null || inspection.Assignments.Count == 0)
            return null;

        var ordered = inspection.Assignments
            .OrderBy(x => x.Sort)
            .ToList();
        if (ordered.Count == 0)
            return null;

        AiFoundryCourseInspectionAssignmentDto? anchor = null;
        if (inspection.AroundAssignmentId.HasValue)
            anchor = ordered.FirstOrDefault(x => x.Id == inspection.AroundAssignmentId.Value);
        anchor ??= ordered[Math.Clamp(ordered.Count / 2, 0, ordered.Count - 1)];
        if (anchor == null)
            return null;

        var nextAssignment = ordered.FirstOrDefault(x => x.Sort > anchor.Sort);
        var reason = inspection.AroundAssignmentId.HasValue && inspection.AroundAssignmentId == anchor.Id
            ? "Берём anchor прямо из inspection: он уже был выбран как точка просмотра соседних заданий."
            : "Аудит не дал явной точки вставки, поэтому берём безопасный fallback anchor из inspection и продолжаем без лишнего запроса к пользователю.";

        return new AiFoundryAgentPlacementCandidateDto
        {
            Source = "course-inspection",
            Concept = "inspection-anchor",
            AfterAssignmentId = anchor.Id,
            AfterAssignmentTitle = anchor.Title,
            BeforeAssignmentTitle = nextAssignment?.Title,
            Reason = reason,
            TaskCount = 6,
            Difficulty = Math.Clamp(anchor.Difficulty, 1, 3),
            TaskFormat = "guided-walkthrough",
            TitleHint = anchor.Title,
        };
    }

    private static AiFoundryCourseAuditDto EnsureBridgeAudit(
        Guid courseId,
        AiFoundryCourseAuditDto? audit,
        AiFoundryCourseInspectionDto? inspection,
        AiFoundryBridgePlanDto? bridgePlan)
    {
        if (audit != null && audit.CourseId == courseId)
            return audit;

        var courseTitle = inspection?.CourseTitle ?? bridgePlan?.CourseTitle ?? "Курс";
        var titleExamples = new List<string>();
        if (inspection != null)
            titleExamples.AddRange(inspection.Assignments.Select(x => x.Title).Where(x => !string.IsNullOrWhiteSpace(x)).Take(12)!);
        if (bridgePlan != null)
            titleExamples.AddRange(bridgePlan.Items.Select(x => x.TitleHint).Where(x => !string.IsNullOrWhiteSpace(x)).Take(12)!);
        titleExamples = titleExamples.Distinct(StringComparer.OrdinalIgnoreCase).Take(12).ToList();

        return new AiFoundryCourseAuditDto
        {
            CourseId = courseId,
            CourseTitle = courseTitle,
            Focus = bridgePlan?.Focus ?? inspection?.Query,
            GeneratedAtUtc = DateTime.UtcNow,
            Summary = inspection != null
                ? $"Синтетический аудит на основе inspection-данных ({inspection.Assignments.Count} заданий)."
                : "Синтетический аудит для bridge-плана без полного аудита.",
            StyleHints = DetectTitleStyleHints(
                (inspection?.Assignments ?? new List<AiFoundryCourseInspectionAssignmentDto>())
                    .Select(x => new CourseAuditAssignmentSnapshot { Id = x.Id, Sort = x.Sort, Title = x.Title, Description = x.DescriptionExcerpt, Difficulty = x.Difficulty })
                    .ToList(),
                titleExamples),
            TitleExamples = titleExamples,
            Findings = audit?.Findings?.ToList() ?? new List<AiFoundryCourseAuditFindingDto>(),
        };
    }

    private static AiFoundryBridgePlanDto BuildDetailedBridgePlanFromAnchor(
        AiFoundryCourseAuditDto audit,
        AiFoundryCourseInspectionDto? inspection,
        Guid afterAssignmentId,
        string? afterAssignmentTitle,
        string? focus,
        int requestedCount,
        AiFoundryAgentStateDto? agentState)
    {
        var titleExamples = GetNearbyInspectionTitles(inspection, afterAssignmentId, null)
            .Concat(audit.TitleExamples ?? new List<string>())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .ToList();
        var normalizedFocus = string.IsNullOrWhiteSpace(focus) ? audit.Focus : focus?.Trim();
        var focusText = (normalizedFocus ?? string.Empty).ToLowerInvariant();
        var forBeginners = string.Equals(agentState?.LearnerAudience, "absolute-beginners", StringComparison.OrdinalIgnoreCase)
            || string.Equals(agentState?.LearnerAudience, "young-beginners", StringComparison.OrdinalIgnoreCase)
            || string.Equals(agentState?.PedagogyMode, "guided-simple", StringComparison.OrdinalIgnoreCase);

        // ── Build plan items from user's actual focus/intent, not hardcoded templates ──
        var userIntent = normalizedFocus ?? string.Empty;
        var userIntentShort = userIntent.Length > 120 ? userIntent[..120] + "…" : userIntent;

        // Extract recognizable concept keywords from the focus to name plan items.
        // Each entry: (keyword in focus) → (concept id, short label for titleHint)
        var conceptMap = new (string[] keywords, string concept, string label)[]
        {
            (new[] { "cout", "вывод", "hello world", "привет мир", "print", "printf" }, "output-intro", "Первый вывод на экран"),
            (new[] { "cin", "ввод", "scanf", "getline", "readline", "считыва" }, "input-intro", "Первый ввод с клавиатуры"),
            (new[] { "перемен", "variable", "int ", "double ", "float ", "тип дан", "объяви" }, "variables", "Знакомство с переменными"),
            (new[] { "строк", "string", "текст", "символ", "char " }, "strings", "Работа с текстом"),
            (new[] { "if", "услови", "ветвлен", "сравнени" }, "conditions", "Простое условие"),
            (new[] { "цикл", "for", "while", "повтор" }, "loops", "Первый цикл"),
            (new[] { "массив", "array", "vector", "вектор" }, "arrays", "Первый массив"),
            (new[] { "функци", "function", "метод", "void ", "return" }, "functions", "Первая функция"),
            (new[] { "класс", "class", "объект", "struct" }, "classes", "Первый класс"),
            (new[] { "указател", "pointer", "ссылк", "reference" }, "pointers", "Указатели и ссылки"),
            (new[] { "введен", "основ", "начал", "знакомств", "intro" }, "intro", "Введение в тему"),
        };

        var detectedConcepts = conceptMap
            .Where(cm => cm.keywords.Any(kw => focusText.Contains(kw)))
            .Select(cm => (cm.concept, cm.label))
            .ToList();

        var count = Math.Clamp(requestedCount, 1, 12);
        List<AiFoundryBridgePlanItemDto> items;

        if (detectedConcepts.Count > 0)
        {
            // Build items from detected concepts — each concept gets at least 1 slot,
            // remaining slots distributed as progressive practice on those concepts.
            var slots = new List<(string concept, string label, int phase)>();
            // Phase 1: one item per detected concept
            foreach (var (concept, label) in detectedConcepts)
                slots.Add((concept, label, 1));
            // Phase 2+: repeat concepts for practice (progressive depth) if there are more slots
            var phase = 2;
            while (slots.Count < count)
            {
                foreach (var (concept, label) in detectedConcepts)
                {
                    if (slots.Count >= count) break;
                    slots.Add((concept + $"-practice-{phase}", $"{label} — закрепление {phase - 1}", phase));
                }
                phase++;
                if (phase > 6) break; // safety cap
            }
            slots = slots.Take(count).ToList();

            items = slots.Select((slot, idx) => new AiFoundryBridgePlanItemDto
            {
                Index = idx + 1,
                Concept = slot.concept,
                AfterAssignmentId = afterAssignmentId,
                AfterAssignmentTitle = afterAssignmentTitle,
                BeforeAssignmentId = null,
                BeforeAssignmentTitle = null,
                Reason = $"Пользователь запросил: «{userIntentShort}». Мостик #{idx + 1} ({slot.label}) — {(slot.phase == 1 ? "вводит тему" : "закрепляет и углубляет")}, после «{afterAssignmentTitle ?? "выбранного задания"}». Генерируй задачу именно по этой теме, а не по общим шаблонам ввода-вывода.",
                TaskCount = 1,
                Difficulty = forBeginners && idx < 3 ? 1 : Math.Min(2, idx / 2 + 1),
                TitleHint = slot.label,
                TitleExamples = titleExamples,
                Confirmed = false,
                Rejected = false,
                UpdatedAtUtc = DateTime.UtcNow,
            }).ToList();
        }
        else if (!string.IsNullOrWhiteSpace(normalizedFocus))
        {
            // User provided a focus but we didn't match specific concepts.
            // Create N items all about the user's focus with progressive complexity.
            items = Enumerable.Range(0, count).Select(idx => new AiFoundryBridgePlanItemDto
            {
                Index = idx + 1,
                Concept = $"user-focus-step-{idx + 1}",
                AfterAssignmentId = afterAssignmentId,
                AfterAssignmentTitle = afterAssignmentTitle,
                BeforeAssignmentId = null,
                BeforeAssignmentTitle = null,
                Reason = $"Пользователь запросил: «{userIntentShort}». Мостик #{idx + 1} — {(idx == 0 ? "самый простой вводный шаг" : $"шаг {idx + 1}, чуть сложнее предыдущего")} по этой теме после «{afterAssignmentTitle ?? "выбранного задания"}». Генерируй задачу строго по фокусу пользователя.",
                TaskCount = 1,
                Difficulty = forBeginners && idx < 3 ? 1 : Math.Min(2, idx / 2 + 1),
                TitleHint = idx == 0 ? $"Введение: {userIntentShort}" : $"Шаг {idx + 1} по теме: {userIntentShort}",
                TitleExamples = titleExamples,
                Confirmed = false,
                Rejected = false,
                UpdatedAtUtc = DateTime.UtcNow,
            }).ToList();
        }
        else
        {
            // No focus at all — refuse to generate garbage. Return null so the caller asks the user.
            return null!;
        }

        return new AiFoundryBridgePlanDto
        {
            CourseId = audit.CourseId,
            CourseTitle = audit.CourseTitle,
            Focus = normalizedFocus,
            GeneratedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
            Status = "draft",
            Summary = $"Собрала план из {items.Count} мостиков по фокусу «{userIntentShort}» после «{afterAssignmentTitle ?? "выбранного задания"}». Каждый пункт привязан к запросу пользователя.",
            StyleHints = audit.StyleHints?.Distinct(StringComparer.OrdinalIgnoreCase).Take(6).ToList() ?? new List<string>(),
            RevisionNotes = new List<string>(),
            Items = items,
        };
    }

    private static List<AiFoundryCourseAuditFindingDto> SelectAuditFindings(AiFoundryCourseAuditDto audit, JsonObject args)
    {
        var indexes = new List<int>();
        if (args["findingIndexes"] is JsonArray arr)
        {
            foreach (var node in arr)
            {
                if (node == null)
                    continue;
                if (int.TryParse(node.ToString(), out var idx))
                    indexes.Add(idx);
            }
        }

        var focus = ReadString(args, "focus");
        var selected = audit.Findings
            .Where((x, idx) => indexes.Count == 0 || indexes.Contains(idx + 1) || indexes.Contains(idx))
            .Where(x => MatchesFocus(x, focus))
            .ToList();
        return selected.Count > 0 ? selected : audit.Findings.Take(4).ToList();
    }

    private static string BuildCourseAuditSummary(AiFoundryCourseAuditDto report)
    {
        var diagnosticAudit = IsDiagnosticGapAuditIntent(report.Focus) || FocusWantsSyntaxBasics(report.Focus);
        var sb = new StringBuilder();
        sb.Append($"Изучила курс «{report.CourseTitle}». ");
        if (!diagnosticAudit && report.StyleHints.Count > 0)
            sb.Append($"По названиям вижу такой стиль: {string.Join(", ", report.StyleHints.Take(3))}. ");
        if (!diagnosticAudit && report.TitleExamples.Count > 0)
            sb.Append($"Хорошие ориентиры по названиям: {string.Join("; ", report.TitleExamples.Take(4))}. ");
        sb.Append(report.Summary);

        if (report.Findings.Count > 0)
        {
            sb.Append(diagnosticAudit ? "\n\nГде вижу реальные педагогические косяки:" : "\n\nЧто стоит вставить:");
            for (var i = 0; i < report.Findings.Count; i++)
            {
                var f = report.Findings[i];
                var anchor = !string.IsNullOrWhiteSpace(f.AfterAssignmentTitle)
                    ? $"после «{f.AfterAssignmentTitle}»"
                    : "в самом начале курса";
                var before = !string.IsNullOrWhiteSpace(f.BeforeAssignmentTitle)
                    ? $", перед «{f.BeforeAssignmentTitle}»"
                    : string.Empty;
                var idText = f.AfterAssignmentId.HasValue ? $" [afterAssignmentId={f.AfterAssignmentId}]" : string.Empty;
                sb.Append($"\n{i + 1}. {anchor}{before}{idText} — {f.Reason} Предлагаю {Math.Max(1, f.SuggestedTaskCount)} мостик(а).");
            }

            if (diagnosticAudit)
            {
                sb.Append("\n\nЭто уже не старый bridge-plan, а новый список prerequisite-проблем, которые надо чинить отдельно.");
                sb.Append("\nЕсли нужно, следующим шагом можно собрать corrective bridge plan только по этим косякам.");
            }
            else
            {
                sb.Append("\n\nЕсли нужно, я могу ещё отдельно открыть соседние задания и показать конкретные названия/формулировки вокруг точек вставки.");
                sb.Append("\nПосле этого я могу собрать подробный план вставок с afterAssignmentId, количеством задач и title hints.");
                sb.Append("\nЕсли план ок, можно следующим сообщением попросить: «собери план мостиков» или «сгенерируй мостики по этому плану».");
            }
        }

        var resultText = sb.ToString().Trim();
        return resultText.Length <= 1900 ? resultText : resultText[..1900];
    }

    private static string BuildCourseInspectionSummary(AiFoundryCourseInspectionDto report)
    {
        var sb = new StringBuilder();
        sb.Append($"Открыла курс «{report.CourseTitle}». ");
        if (!string.IsNullOrWhiteSpace(report.Query))
            sb.Append($"Фокус просмотра: {report.Query}. ");
        sb.Append(report.Summary);
        if (report.Observations.Count > 0)
        {
            sb.Append("\n\nЧто подтвердилось по реальным условиям:");
            foreach (var item in report.Observations.Take(6))
                sb.Append($"\n- {item}");
        }
        var important = report.Assignments
            .Where(x => x.AiOverview?.IsMeaningful() == true)
            .OrderByDescending(x => (x.AiOverview?.ImportanceScore ?? (x.AiOverview?.IsImportant == true ? 0.75d : 0.0d)) + ((x.AiOverview?.PedagogicalRole ?? string.Empty).Equals("guided-intro", StringComparison.OrdinalIgnoreCase) ? 0.15d : 0d))
            .ThenBy(x => x.Sort)
            .Take(4)
            .ToList();
        if (important.Count > 0)
        {
            sb.Append("\n\nКакие задания выглядят опорными по AI overview:");
            foreach (var item in important)
            {
                var ov = item.AiOverview!;
                var why = ov.ImportanceReasons.FirstOrDefault() ?? ov.CourseValue ?? ov.Summary;
                sb.Append($"\n- sort={item.Sort}: {item.Title} — роль: {ov.PedagogicalRole ?? "не указана"}; важно потому что: {why}");
            }
        }
        if (report.Assignments.Count > 0)
        {
            sb.Append("\n\nЧто просмотрела:");
            foreach (var item in report.Assignments)
                sb.Append($"\n- sort={item.Sort}, assignmentId={item.Id}, difficulty={item.Difficulty}: {item.Title} — {item.DescriptionExcerpt}");
        }
        var result = sb.ToString().Trim();
        return result.Length <= 1900 ? result : result[..1900];
    }

    private static AiFoundryBridgePlanDto BuildBridgePlan(
        AiFoundryCourseAuditDto audit,
        AiFoundryCourseInspectionDto? inspection,
        List<AiFoundryCourseAuditFindingDto> findings,
        string? focus,
        Guid? requestedAfterAssignmentId = null,
        int requestedCount = 6)
    {
        var normalizedFocus = string.IsNullOrWhiteSpace(focus) ? audit.Focus : focus?.Trim();
        var filteredFindings = findings
            .Where(x => MatchesFocus(x, normalizedFocus))
            // Accept findings with null AfterAssignmentId (gap before first assignment)
            // alongside findings matching the requested anchor
            .Where(x => !requestedAfterAssignmentId.HasValue || x.AfterAssignmentId == requestedAfterAssignmentId || !x.AfterAssignmentId.HasValue)
            .ToList();
        if (filteredFindings.Count == 0 && requestedAfterAssignmentId.HasValue)
        {
            var matched = findings.FirstOrDefault(x => x.AfterAssignmentId == requestedAfterAssignmentId);
            if (matched != null)
                filteredFindings.Add(matched);
        }
        // Last resort: take first findings as-is — they passed ShouldFlagConcept so they're valid
        if (filteredFindings.Count == 0 && findings.Count > 0)
            filteredFindings = findings.Take(Math.Min(findings.Count, requestedCount)).ToList();

        var items = filteredFindings
            .Select((finding, idx) =>
            {
                var nearbyExamples = GetNearbyInspectionTitles(inspection, finding.AfterAssignmentId, finding.BeforeAssignmentId);
                var titleExamples = nearbyExamples
                    .Concat(audit.TitleExamples ?? new List<string>())
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(6)
                    .ToList();
                return new AiFoundryBridgePlanItemDto
                {
                    Index = idx + 1,
                    Concept = finding.Concept,
                    AfterAssignmentId = finding.AfterAssignmentId,
                    AfterAssignmentTitle = finding.AfterAssignmentTitle,
                    BeforeAssignmentId = finding.BeforeAssignmentId,
                    BeforeAssignmentTitle = finding.BeforeAssignmentTitle,
                    Reason = finding.Reason,
                    TaskCount = Math.Max(1, finding.SuggestedTaskCount),
                    Difficulty = Math.Clamp(finding.SuggestedDifficulty ?? 2, 1, 3),
                    TitleHint = BuildBridgeTitleHint(finding, titleExamples),
                    TitleExamples = titleExamples,
                    Confirmed = false,
                    Rejected = false,
                    UpdatedAtUtc = DateTime.UtcNow,
                };
            })
            .ToList();

        var summary = items.Count == 0
            ? "Не удалось собрать план мостиков по текущему аудиту."
            : $"Собрала план мостиков на {items.Sum(x => x.TaskCount)} задач(и) в {items.Count} точках вставки. Для каждой точки есть afterAssignmentId, причина и title hint в стиле курса.";

        return new AiFoundryBridgePlanDto
        {
            CourseId = audit.CourseId,
            CourseTitle = audit.CourseTitle,
            Focus = normalizedFocus,
            GeneratedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
            Status = "draft",
            Summary = summary,
            StyleHints = audit.StyleHints?.Distinct(StringComparer.OrdinalIgnoreCase).Take(6).ToList() ?? new List<string>(),
            RevisionNotes = new List<string>(),
            Items = items,
        };
    }

    private async Task<AiFoundryBridgePlanDto> ReviseBridgePlanAsync(
        AiFoundryBridgePlanDto plan,
        AiFoundryCourseInspectionDto? inspection,
        JsonObject args,
        CancellationToken ct)
    {
        var revised = CloneBridgePlan(plan);
        var indexes = ReadIndexes(args, "itemIndexes", "itemIndex", "findingIndexes");
        if (indexes.Count == 0)
            indexes = InferPlanItemIndexesFromText(ReadString(args, "note") ?? ReadString(args, "reason"));
        var normalizedIndexes = indexes
            .Select(x => x <= 0 ? 1 : x)
            .Distinct()
            .ToHashSet();
        var selected = revised.Items
            .Where(x => normalizedIndexes.Count == 0 || normalizedIndexes.Contains(x.Index))
            .ToList();
        if (selected.Count == 0)
            selected = revised.Items.Take(1).ToList();

        var note = ReadString(args, "note");
        var reason = ReadString(args, "reason");
        var titleHint = ReadString(args, "titleHint");
        var taskCount = ReadInt(args, "taskCount") ?? ReadInt(args, "count");
        var difficulty = ReadInt(args, "difficulty");
        var afterAssignmentId = ReadGuid(args, "afterAssignmentId");
        var confirm = ReadBool(args, "confirm") == true;
        var reject = ReadBool(args, "reject") == true;
        string? afterTitle = null;

        if (afterAssignmentId.HasValue)
        {
            afterTitle = inspection?.Assignments.FirstOrDefault(x => x.Id == afterAssignmentId.Value)?.Title;
            if (string.IsNullOrWhiteSpace(afterTitle))
            {
                afterTitle = await _db.TaskAssignments.AsNoTracking()
                    .Where(x => x.Id == afterAssignmentId.Value)
                    .Select(x => x.Title)
                    .FirstOrDefaultAsync(ct);
            }
        }

        foreach (var item in selected)
        {
            if (taskCount.HasValue)
                item.TaskCount = Math.Clamp(taskCount.Value, 1, 8);
            if (difficulty.HasValue)
                item.Difficulty = Math.Clamp(difficulty.Value, 1, 5);
            if (!string.IsNullOrWhiteSpace(titleHint))
                item.TitleHint = ShortenSingleLine(titleHint, 100);
            if (!string.IsNullOrWhiteSpace(reason))
                item.Reason = ShortenSingleLine(reason, 240);
            if (afterAssignmentId.HasValue)
            {
                item.AfterAssignmentId = afterAssignmentId;
                if (!string.IsNullOrWhiteSpace(afterTitle))
                    item.AfterAssignmentTitle = afterTitle;
            }
            if (confirm)
            {
                item.Confirmed = true;
                item.Rejected = false;
            }
            if (reject)
            {
                item.Rejected = true;
                item.Confirmed = false;
            }
            if (!string.IsNullOrWhiteSpace(note))
                item.RevisionNote = ShortenSingleLine(note, 280);
            item.UpdatedAtUtc = DateTime.UtcNow;
        }

        if (!string.IsNullOrWhiteSpace(note))
            revised.RevisionNotes.Add(ShortenSingleLine(note, 280));
        if (!string.IsNullOrWhiteSpace(reason) && string.IsNullOrWhiteSpace(note))
            revised.RevisionNotes.Add($"Причина правки: {ShortenSingleLine(reason, 220)}");

        revised.UpdatedAtUtc = DateTime.UtcNow;
        var activeCount = revised.Items.Count(x => !x.Rejected);
        var confirmedCount = revised.Items.Count(x => x.Confirmed && !x.Rejected);
        if (activeCount > 0 && confirmedCount == activeCount)
        {
            revised.Status = "confirmed";
            revised.ConfirmedAtUtc = DateTime.UtcNow;
        }
        else if (confirmedCount > 0)
        {
            revised.Status = "partially-confirmed";
            revised.ConfirmedAtUtc ??= DateTime.UtcNow;
        }
        else
        {
            revised.Status = revised.Items.Any(x => x.Rejected) ? "revised" : "draft";
        }

        var taskTotal = revised.Items.Where(x => !x.Rejected).Sum(x => Math.Max(1, x.TaskCount));
        var activeItems = revised.Items.Count(x => !x.Rejected);
        var rejectedCount = revised.Items.Count(x => x.Rejected);
        revised.Summary = activeItems == 0
            ? "План мостиков очищен: активных точек вставки не осталось."
            : $"План мостиков обновлён: активных точек {activeItems}, задач суммарно {taskTotal}, подтверждено {confirmedCount}, отклонено {rejectedCount}.";

        return revised;
    }

    private static AiFoundryBridgePlanDto CloneBridgePlan(AiFoundryBridgePlanDto plan)
        => new()
        {
            CourseId = plan.CourseId,
            CourseTitle = plan.CourseTitle,
            Focus = plan.Focus,
            GeneratedAtUtc = plan.GeneratedAtUtc,
            UpdatedAtUtc = plan.UpdatedAtUtc,
            ConfirmedAtUtc = plan.ConfirmedAtUtc,
            Status = plan.Status,
            Summary = plan.Summary,
            StyleHints = plan.StyleHints.ToList(),
            RevisionNotes = plan.RevisionNotes.ToList(),
            Items = plan.Items.Select(x => new AiFoundryBridgePlanItemDto
            {
                Index = x.Index,
                Concept = x.Concept,
                AfterAssignmentId = x.AfterAssignmentId,
                AfterAssignmentTitle = x.AfterAssignmentTitle,
                BeforeAssignmentId = x.BeforeAssignmentId,
                BeforeAssignmentTitle = x.BeforeAssignmentTitle,
                Reason = x.Reason,
                TaskCount = x.TaskCount,
                Difficulty = x.Difficulty,
                TitleHint = x.TitleHint,
                TitleExamples = x.TitleExamples.ToList(),
                Confirmed = x.Confirmed,
                Rejected = x.Rejected,
                RevisionNote = x.RevisionNote,
                UpdatedAtUtc = x.UpdatedAtUtc,
            }).ToList(),
        };

    private static List<AiFoundryBridgePlanItemDto> SelectBridgePlanItems(AiFoundryBridgePlanDto plan, JsonObject args)
    {
        var indexes = ReadIndexes(args, "itemIndexes", "itemIndex", "findingIndexes");
        var focus = ReadString(args, "focus") ?? plan.Focus;
        var candidateItems = plan.Items.Where(x => !x.Rejected).ToList();
        if (indexes.Count == 0)
        {
            var confirmed = candidateItems.Where(x => x.Confirmed).ToList();
            if (confirmed.Count > 0)
                candidateItems = confirmed;
        }
        var selected = candidateItems
            .Where(x => indexes.Count == 0 || indexes.Contains(x.Index) || indexes.Contains(x.Index - 1))
            .Where(x => string.IsNullOrWhiteSpace(focus) || MatchesPlanItemFocus(x, focus))
            .ToList();
        return selected.Count > 0 ? selected : candidateItems.Take(4).ToList();
    }

    private static string BuildBridgePlanSummary(AiFoundryBridgePlanDto plan)
    {
        var sb = new StringBuilder();
        sb.Append($"План мостиков для курса «{plan.CourseTitle}». ");
        if (!string.IsNullOrWhiteSpace(plan.Focus))
            sb.Append($"Фокус: {plan.Focus}. ");
        if (plan.StyleHints.Count > 0)
            sb.Append($"Стиль курса: {string.Join(", ", plan.StyleHints.Take(3))}. ");
        if (!string.IsNullOrWhiteSpace(plan.Status))
            sb.Append($"Статус плана: {plan.Status}. ");
        sb.Append(plan.Summary);

        if (plan.Items.Count > 0)
        {
            sb.Append("\n\nПлан вставок:");
            foreach (var item in plan.Items)
            {
                var marker = item.Rejected ? "[отклонено] " : item.Confirmed ? "[подтверждено] " : string.Empty;
                var anchor = !string.IsNullOrWhiteSpace(item.AfterAssignmentTitle)
                    ? $"после «{item.AfterAssignmentTitle}»"
                    : "в начало курса";
                var before = !string.IsNullOrWhiteSpace(item.BeforeAssignmentTitle)
                    ? $", перед «{item.BeforeAssignmentTitle}»"
                    : string.Empty;
                var idText = item.AfterAssignmentId.HasValue ? $" [afterAssignmentId={item.AfterAssignmentId}]" : string.Empty;
                var note = !string.IsNullOrWhiteSpace(item.RevisionNote) ? $" Примечание: {item.RevisionNote}." : string.Empty;
                sb.Append($"\n{item.Index}. {marker}{anchor}{before}{idText} — {item.Reason} Набор: {item.TaskCount} задач(и), сложность {item.Difficulty}, title hint: «{item.TitleHint}».{note}");
            }
            if (plan.RevisionNotes.Count > 0)
                sb.Append($"\n\nПоследние правки: {string.Join(" | ", plan.RevisionNotes.TakeLast(3))}.");
            sb.Append("\n\nЕсли хочешь, я могу точечно поправить этот план по замечаниям, подтвердить нужные пункты или сразу сгенерировать прямую серию мостиков только по подтверждённым точкам.");
        }

        var result = sb.ToString().Trim();
        return result.Length <= 2600 ? result : result[..2600];
    }

    private static string BuildBridgeBatchPrompt(
        AiFoundryCourseAuditDto audit,
        AiFoundryBridgePlanDto plan,
        List<AiFoundryBridgePlanItemDto> items,
        AiFoundryChatMemoryDto memory,
        string? userPrompt,
        string? focus)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(userPrompt))
            sb.AppendLine(userPrompt.Trim());
        else
            sb.AppendLine($"Нужно допилить курс «{audit.CourseTitle}» bridge-задачами, которые мягко вводят новые функции и конструкции до их резкого появления.");
        if (!string.IsNullOrWhiteSpace(focus ?? plan.Focus))
            sb.AppendLine($"Фокус: {(focus ?? plan.Focus)!.Trim()}.");
        if (!string.IsNullOrWhiteSpace(memory.LatestExplicitInstruction))
            sb.AppendLine($"Последняя явная инструкция пользователя: {memory.LatestExplicitInstruction.Trim()}.");
        if (!string.IsNullOrWhiteSpace(memory.LatestTeachingScript))
        {
            sb.AppendLine("У пользователя уже есть почти готовый teaching-script/эталон. Не сворачивай его в абстрактный microGoal: сохраняй порядок объяснения, примеры строк кода и смысл каждой строки.");
            sb.AppendLine("Latest teaching-script:");
            sb.AppendLine(memory.LatestTeachingScript.Trim());
        }
        sb.AppendLine("Работай по согласованному плану вставок, а не придумывай точки размещения с нуля.");
        sb.AppendLine("Если пользователь уже дал почти готовую обучалку, новая задача должна следовать именно ей. Нельзя писать 'создай переменную' или 'считай число', пока в самой задаче не объяснено, как это сделать строка за строкой.");
        sb.AppendLine("Используй стиль названий и формулировок текущего курса, избегай абстрактных и слишком общих названий.");
        if (plan.StyleHints.Count > 0)
            sb.AppendLine($"Стиль названий курса: {string.Join("; ", plan.StyleHints.Take(4))}.");
        if (audit.TitleExamples.Count > 0)
            sb.AppendLine($"Примеры живых названий из курса: {string.Join("; ", audit.TitleExamples.Take(6))}.");
        sb.AppendLine("Не используй расплывчатые названия вроде «Форматированный вывод», «Работа со строкой», «Базовый ввод» и подобные. Название должно быть таким же конкретным, как у существующих заданий курса.");
        sb.AppendLine("ВАЖНО: Приоритет №1 — фокус и инструкция пользователя выше. Если пользователь просил конкретную тему (например, 'объяснение cout'), генерируй именно это, а НЕ подменяй абстрактными задачами на ввод/вывод или другие темы.");
        sb.AppendLine("Для каждого нового задания обязательно выбери placementAfterAssignmentId из plan items ниже.");
        sb.AppendLine("Plan items:");
        foreach (var item in items)
        {
            var examples = item.TitleExamples.Count == 0 ? "-" : string.Join(" | ", item.TitleExamples.Take(4));
            sb.AppendLine($"- item #{item.Index}: afterAssignmentId={item.AfterAssignmentId}; afterTitle={item.AfterAssignmentTitle}; beforeTitle={item.BeforeAssignmentTitle}; concept={item.Concept}; reason={item.Reason}; taskCount={item.TaskCount}; difficulty={item.Difficulty}; titleHint={item.TitleHint}; nearbyTitleExamples={examples}.");
        }
        sb.AppendLine("Не копируй существующие задания дословно (точное название + тот же смысл). Похожие задачи с другим акцентом — допустимы. Сгенерируй именно вводящие мостики, а не ещё один общий topic-pack.");

        // ── Explicit dedup blocklist: existing course task titles ──
        var existingTitles = new List<string>();
        if (audit.TitleExamples != null)
            existingTitles.AddRange(audit.TitleExamples);
        // Pull full assignment list from inspection (all course tasks)
        if (memory.LastCourseInspection != null)
            existingTitles.AddRange(memory.LastCourseInspection.Assignments.Select(x => x.Title));
        // Also pull from plan item title examples
        foreach (var item in items)
        {
            if (item.TitleExamples != null)
                existingTitles.AddRange(item.TitleExamples);
        }
        existingTitles = existingTitles
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .ToList();
        if (existingTitles.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("==== ЗАДАНИЯ, КОТОРЫЕ УЖЕ ЕСТЬ В КУРСЕ ====");
            foreach (var title in existingTitles)
                sb.AppendLine($"  • {title}");
            sb.AppendLine("Похожие задачи допустимы, если они отличаются хотя бы немного (другие числа, другой контекст, другой акцент). Но НЕ создавай точных копий с тем же названием и тем же смыслом.");
        }

        return sb.ToString().Trim();
    }

    private static string BuildBridgeBatchNotes(AiFoundryCourseAuditDto audit, AiFoundryBridgePlanDto plan, List<AiFoundryBridgePlanItemDto> items)
    {
        var joined = string.Join(" | ", items.Select(x => $"idx={x.Index}; after={x.AfterAssignmentId}; titleHint={x.TitleHint}; concept={x.Concept}"));
        var value = $"agent-bridge-pack; auditAt={audit.GeneratedAtUtc:O}; planAt={plan.GeneratedAtUtc:O}; items={joined}";
        return value.Length <= 1800 ? value : value[..1800];
    }

    private static List<string> GetNearbyInspectionTitles(AiFoundryCourseInspectionDto? inspection, Guid? afterAssignmentId, Guid? beforeAssignmentId)
    {
        if (inspection == null || inspection.Assignments.Count == 0)
            return new List<string>();

        var ordered = inspection.Assignments
            .OrderByDescending(x => afterAssignmentId.HasValue && x.Id == afterAssignmentId.Value)
            .ThenByDescending(x => beforeAssignmentId.HasValue && x.Id == beforeAssignmentId.Value)
            .ThenBy(x => x.Sort)
            .Select(x => (x.Title ?? string.Empty).Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(6)
            .ToList();
        return ordered;
    }

    private static bool MatchesPlanItemFocus(AiFoundryBridgePlanItemDto item, string? focus)
    {
        var text = string.Join(" ", new[] { item.Concept, item.Reason, item.TitleHint, item.AfterAssignmentTitle, item.BeforeAssignmentTitle });
        return MatchesFocus(text, focus);
    }

    private static string BuildBridgeTitleHint(AiFoundryCourseAuditFindingDto finding, List<string> titleExamples)
    {
        var concept = (finding.Concept ?? string.Empty).Trim().ToLowerInvariant();
        string hint = concept switch
        {
            "getline" => "Строка с пробелами",
            "printf/scanf" => "scanf и printf",
            "fixed/setprecision" => "Вывод числа с точностью",
            "cout" => "Первый вывод через cout",
            "cin" => "Первый ввод через cin",
            "variables" => "Первая переменная",
            "program-structure" => "Самая базовая программа",
            "string" => "Строка и длина",
            "if" => "Проверка условия",
            _ => !string.IsNullOrWhiteSpace(finding.BeforeAssignmentTitle) ? finding.BeforeAssignmentTitle! : "Подводящее задание",
        };

        var normalizedExamples = titleExamples
            .Select(x => Regex.Replace(x, @"^Задание\s+\d+[\.:]?\s*", string.Empty, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();
        if (normalizedExamples.Any(x => x.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 5))
        {
            var example = normalizedExamples.FirstOrDefault(x => x.Contains("вывод", StringComparison.OrdinalIgnoreCase) || x.Contains("строк", StringComparison.OrdinalIgnoreCase) || x.Contains("числ", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(example) && hint.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 5)
                hint = example!;
        }
        hint = hint.Replace("Форматированный вывод", "Вывод числа").Replace("Работа со строкой", "Строка целиком").Trim();
        return ShortenSingleLine(hint, 80);
    }

    private static List<string> DetectTitleStyleHints(List<CourseAuditAssignmentSnapshot> ordered, List<string> titleExamples)
    {
        var hints = new List<string>();
        if (ordered.Count == 0)
            return hints;
        if (ordered.Count(x => Regex.IsMatch(x.Title ?? string.Empty, @"^Задание\s+\d+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) >= Math.Max(2, ordered.Count / 3))
            hints.Add("часто используются нумерованные названия вида «Задание N. ...»");
        if (ordered.Count(x => (x.Title ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 4) >= Math.Max(2, ordered.Count / 3))
            hints.Add("заголовки короткие и конкретные");
        if (ordered.Count(x => Regex.IsMatch(x.Title ?? string.Empty, @"cout|cin|printf|scanf|строк|числ|вывод|ввод", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) >= Math.Max(2, ordered.Count / 4))
            hints.Add("в названии часто явно упоминается действие или объект ввода/вывода");
        if (titleExamples.Any(x => x.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length <= 5))
            hints.Add("нужно избегать слишком общих книжных названий и держать заголовок на уровне конкретного действия");
        return hints.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static HashSet<string> ExtractCourseConcepts(string? raw, AiAssignmentOverviewDto? overview = null)
    {
        var text = ExtractPlainTextFromRichDescription(raw).ToLowerInvariant();
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void add(string key, params string[] needles)
        {
            if (needles.Any(n => text.Contains(n)))
                set.Add(key);
        }

        add("cout", "cout");
        add("cin", "cin");
        if (ContainsVariableDeclaration(text))
            set.Add("variables");
        if (ContainsProgramStructure(text))
            set.Add("program-structure");
        add("printf/scanf", "printf", "scanf");
        add("getline", "getline", "строку с пробел", "строки с пробел", "строки целиком");
        if (Regex.IsMatch(text, @"\bstring\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            || text.Contains("тип string")
            || text.Contains("класс string"))
            set.Add("string");
        add("fixed/setprecision", "setprecision", "fixed", "форматированн");
        if (Regex.IsMatch(text, @"\bif\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            || text.Contains("ветвлен")
            || text.Contains("условный оператор"))
            set.Add("if");
        if (Regex.IsMatch(text, @"\bfor\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            || text.Contains("цикл for"))
            set.Add("for");
        if (Regex.IsMatch(text, @"\bwhile\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            || text.Contains("цикл while"))
            set.Add("while");
        add("sqrt/pow", "sqrt", "pow");
        add("abs", "abs(", "std::abs", "модул");
        foreach (Match m in Regex.Matches(text, @"\b([a-z_][a-z0-9_]*)\s*\(", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            var name = m.Groups[1].Value;
            if (name.Length < 3)
                continue;
            if (name is "main" or "solve" or "print" or "input")
                continue;
            set.Add(name);
        }

        if (overview != null)
        {
            foreach (var concept in overview.ConceptsIntroduced.Concat(overview.ConceptsReinforced).Concat(overview.Prerequisites))
            {
                var normalized = NormalizeOverviewConcept(concept);
                if (!string.IsNullOrWhiteSpace(normalized))
                    set.Add(normalized!);
            }
        }

        return set;
    }

    private static string ExtractPlainTextFromRichDescription(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        var normalized = NormalizeRichText(raw);
        if (!string.IsNullOrWhiteSpace(normalized))
            return normalized;

        return raw.Replace("\r", " ").Replace("\n", " ").Trim();
    }

    private static string NormalizeRichText(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        var fragments = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(raw);
            CollectRichTextFragments(doc.RootElement, fragments);
        }
        catch
        {
            return raw!.Replace("\r", " ").Replace("\n", " ").Trim();
        }

        return string.Join(" ", fragments.Where(x => !string.IsNullOrWhiteSpace(x))).Trim();
    }

    private static void CollectRichTextFragments(JsonElement element, List<string> parts)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (element.TryGetProperty("text", out var textProp) && textProp.ValueKind == JsonValueKind.String)
                    parts.Add(textProp.GetString() ?? string.Empty);
                foreach (var property in element.EnumerateObject())
                    CollectRichTextFragments(property.Value, parts);
                break;
            case JsonValueKind.Array:
                foreach (var child in element.EnumerateArray())
                    CollectRichTextFragments(child, parts);
                break;
            case JsonValueKind.String:
                var value = element.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                    parts.Add(value);
                break;
        }
    }

    private static bool ContainsVariableDeclaration(string text)
        => Regex.IsMatch(text, @"\b(int|double|float|long\s+long|char|bool|string)\s+[a-z_][a-z0-9_]*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            || text.Contains("объяви переменную")
            || text.Contains("создай переменную")
            || text.Contains("переменн");

    private static bool ContainsProgramStructure(string text)
        => text.Contains("#include")
            || text.Contains("using namespace std")
            || text.Contains("int main")
            || text.Contains("main()")
            || text.Contains("каркас программы")
            || text.Contains("структура программы");

    private static bool IsGuidedIntroAssignment(CourseAuditAssignmentSnapshot snapshot)
    {
        var overview = snapshot.AiOverview;
        if (!string.IsNullOrWhiteSpace(overview?.PedagogicalRole) && overview.PedagogicalRole.Contains("guided", StringComparison.OrdinalIgnoreCase))
            return true;
        if (!string.IsNullOrWhiteSpace(overview?.TeachingStyle) && (overview.TeachingStyle.Contains("step", StringComparison.OrdinalIgnoreCase) || overview.TeachingStyle.Contains("guided", StringComparison.OrdinalIgnoreCase)))
            return true;

        var text = $"{snapshot.Title}\n{ExtractPlainTextFromRichDescription(snapshot.Description)}".ToLowerInvariant();
        return text.Contains("следуй шагам")
            || text.Contains("давай")
            || text.Contains("напиши в самом начале")
            || text.Contains("внутри напиши")
            || text.Contains("закрой программу")
            || text.Contains("эта строка подключает")
            || text.Contains("это начало главной части программы");
    }

    private static bool AssignmentIntroducesConcept(CourseAuditAssignmentSnapshot snapshot, string concept)
    {
        var text = $"{snapshot.Title}\n{ExtractPlainTextFromRichDescription(snapshot.Description)}".ToLowerInvariant();
        if (!IsGuidedIntroAssignment(snapshot))
            return false;

        var overviewConcepts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (snapshot.AiOverview != null)
        {
            foreach (var item in snapshot.AiOverview.ConceptsIntroduced.Concat(snapshot.AiOverview.ConceptsReinforced))
            {
                var normalized = NormalizeOverviewConcept(item);
                if (!string.IsNullOrWhiteSpace(normalized))
                    overviewConcepts.Add(normalized!);
            }
        }

        return concept switch
        {
            "cout" => text.Contains("cout") || overviewConcepts.Contains("cout"),
            "program-structure" => text.Contains("#include") || text.Contains("using namespace std") || text.Contains("main") || overviewConcepts.Contains("program-structure"),
            "variables" => ContainsVariableDeclaration(text) || text.Contains("переменн") || overviewConcepts.Contains("variables"),
            "printf/scanf" => text.Contains("printf") || text.Contains("scanf") || overviewConcepts.Contains("printf/scanf"),
            "cin" => text.Contains("cin") || overviewConcepts.Contains("cin"),
            _ => overviewConcepts.Contains(concept),
        };
    }

    private static List<CourseAuditAssignmentSnapshot> SelectImportantInspectionSeeds(List<CourseAuditAssignmentSnapshot> ordered, int take)
    {
        return ordered
            .Where(x => x.AiOverview != null && x.AiOverview.IsMeaningful())
            .OrderByDescending(GetAssignmentLandmarkScore)
            .ThenBy(x => x.Sort)
            .Take(Math.Clamp(take, 1, 8))
            .ToList();
    }

    private static List<string> BuildInspectionObservations(List<CourseAuditAssignmentSnapshot> ordered)
    {
        var observations = new List<string>();
        if (ordered.Count == 0)
            return observations;

        var important = ordered
            .Where(x => x.AiOverview?.IsMeaningful() == true)
            .OrderByDescending(GetAssignmentLandmarkScore)
            .ThenBy(x => x.Sort)
            .Take(2)
            .ToList();
        foreach (var item in important)
        {
            var ov = item.AiOverview!;
            if (!string.IsNullOrWhiteSpace(ov.PedagogicalRole))
            {
                var why = ov.ImportanceReasons.FirstOrDefault() ?? ov.CourseValue ?? ov.Summary;
                observations.Add($"«{item.Title}» AI overview помечает как {ov.PedagogicalRole}; это опорная точка курса, поэтому её надо учитывать при поиске пробелов и мостиков.");
                if (!string.IsNullOrWhiteSpace(why))
                    observations.Add($"Почему «{item.Title}» важно: {why}");
            }
        }

        var first = ordered.OrderBy(x => x.Sort).First();
        var firstText = $"{first.Title}\n{ExtractPlainTextFromRichDescription(first.Description)}".ToLowerInvariant();
        if (AssignmentIntroducesConcept(first, "cout"))
            observations.Add($"«{first.Title}» уже выглядит как пошаговая обучалка по cout, поэтому тут нельзя честно говорить, что вывод через cout появился совсем без введения.");
        if (AssignmentIntroducesConcept(first, "program-structure"))
            observations.Add($"«{first.Title}» уже объясняет каркас программы (#include / using namespace std / main) в самом условии.");
        if (firstText.Contains("printf") && !AssignmentIntroducesConcept(first, "printf/scanf"))
            observations.Add($"«{first.Title}» уже использует printf/scanf, но без такой же пошаговой подводки, как у первого guided-intro задания.");

        var firstPrintf = ordered.FirstOrDefault(x => $"{x.Title}\n{ExtractPlainTextFromRichDescription(x.Description)}".ToLowerInvariant().Contains("printf"));
        if (firstPrintf != null && !AssignmentIntroducesConcept(firstPrintf, "printf/scanf"))
            observations.Add($"«{firstPrintf.Title}» выглядит как первое появление printf/scanf без отдельной детской пошаговой обучалки уровня первого задания.");

        var firstCin = ordered.FirstOrDefault(x => $"{x.Title}\n{ExtractPlainTextFromRichDescription(x.Description)}".ToLowerInvariant().Contains("cin"));
        if (firstCin != null && !AssignmentIntroducesConcept(firstCin, "cin"))
            observations.Add($"«{firstCin.Title}» выглядит как первое прямое требование к cin и вводу данных, его уже можно считать реальным порогом входа.");

        if (observations.Count == 0)
            observations.Add("По реальным условиям видно, что часть стартовых тем уже объясняется в самих заданиях, поэтому голый эвристический аудит нужно перепроверять inspection-ом.");

        return observations.Distinct(StringComparer.OrdinalIgnoreCase).Take(6).ToList();
    }

    private async Task<AiCourseOverviewCoverageDto> BuildCourseOverviewCoverageAsync(Guid courseId, CancellationToken ct)
    {
        var totalAssignments = await _db.TaskAssignments.AsNoTracking()
            .Where(x => x.CourseId == courseId)
            .CountAsync(ct);

        if (totalAssignments <= 0)
        {
            return new AiCourseOverviewCoverageDto
            {
                TotalAssignments = 0,
                AssignmentsWithOverview = 0,
                AssignmentsMissingOverview = 0,
                CoverageRatio = 0d,
            };
        }

        var assignmentsWithOverview = await _db.AiAssignmentInsights.AsNoTracking()
            .Where(x => x.Assignment != null
                && x.Assignment.CourseId == courseId
                && (x.Kind == AiAssignmentOverviewHelper.CourseOverviewKind || x.Kind == AiAssignmentOverviewHelper.LegacyOverviewKind))
            .Select(x => x.AssignmentId)
            .Distinct()
            .CountAsync(ct);

        var missing = Math.Max(0, totalAssignments - assignmentsWithOverview);
        return new AiCourseOverviewCoverageDto
        {
            TotalAssignments = totalAssignments,
            AssignmentsWithOverview = assignmentsWithOverview,
            AssignmentsMissingOverview = missing,
            CoverageRatio = totalAssignments == 0 ? 0d : Math.Round((double)assignmentsWithOverview / totalAssignments, 3),
        };
    }

    private async Task<List<AiCourseLandmarkAssignmentDto>> LoadCourseLandmarkAssignmentsAsync(Guid courseId, int take, CancellationToken ct)
    {
        var maxItems = Math.Clamp(take, 3, 12);
        var ordered = await LoadCourseAuditAssignmentsAsync(courseId, null, ct);
        if (ordered.Count == 0)
            return new List<AiCourseLandmarkAssignmentDto>();

        var candidates = ordered
            .Where(x => x.AiOverview != null && x.AiOverview.IsMeaningful())
            .ToList();
        if (candidates.Count == 0)
            return new List<AiCourseLandmarkAssignmentDto>();

        var strongCandidates = candidates
            .Where(x => x.AiOverview!.IsImportant
                || (x.AiOverview!.ImportanceScore ?? 0d) >= 0.70d
                || string.Equals(x.AiOverview!.PedagogicalRole, "guided-intro", StringComparison.OrdinalIgnoreCase)
                || string.Equals(x.AiOverview!.PedagogicalRole, "bridge", StringComparison.OrdinalIgnoreCase)
                || string.Equals(x.AiOverview!.PedagogicalRole, "milestone", StringComparison.OrdinalIgnoreCase)
                || string.Equals(x.AiOverview!.PedagogicalRole, "assessment", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var selected = (strongCandidates.Count >= Math.Min(3, maxItems) ? strongCandidates : candidates)
            .OrderByDescending(GetAssignmentLandmarkScore)
            .ThenBy(x => x.Sort)
            .Take(maxItems)
            .Select(x => new AiCourseLandmarkAssignmentDto
            {
                Id = x.Id,
                Sort = x.Sort,
                Difficulty = x.Difficulty,
                Title = x.Title,
                Type = x.Type,
                AiOverview = x.AiOverview,
            })
            .ToList();

        return selected;
    }

    private static double GetAssignmentLandmarkScore(CourseAuditAssignmentSnapshot snapshot)
    {
        var overview = snapshot.AiOverview;
        if (overview == null)
            return 0d;

        var score = overview.ImportanceScore ?? (overview.IsImportant ? 0.75d : 0.35d);
        if (overview.IsImportant)
            score += 0.15d;

        var role = (overview.PedagogicalRole ?? string.Empty).Trim().ToLowerInvariant();
        score += role switch
        {
            "guided-intro" => 0.30d,
            "milestone" => 0.26d,
            "assessment" => 0.22d,
            "bridge" => 0.18d,
            "reference" => 0.14d,
            "skill-drill" => 0.08d,
            _ => 0d,
        };

        if (overview.ConceptsIntroduced.Count > 0)
            score += Math.Min(0.12d, overview.ConceptsIntroduced.Count * 0.03d);
        if (!string.IsNullOrWhiteSpace(overview.CourseValue))
            score += 0.05d;
        if (snapshot.Sort <= 3)
            score += 0.05d;

        return score;
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

    private static string? NormalizeOverviewConcept(string? raw)
    {
        var text = (raw ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(text))
            return null;
        if (_knownPedagogicalConcepts.Contains(text))
            return text;
        if (text.Contains("cout")) return "cout";
        if (text.Contains("cin")) return "cin";
        if (text.Contains("printf") || text.Contains("scanf")) return "printf/scanf";
        if (text.Contains("переменн") || text.Contains("variable")) return "variables";
        if (text.Contains("main") || text.Contains("#include") || text.Contains("namespace") || text.Contains("каркас")) return "program-structure";
        if (text.Contains("getline")) return "getline";
        if (text == "string" || text.Contains("строк")) return "string";
        if (text.Contains("setprecision") || text.Contains("fixed") || text.Contains("форматирован")) return "fixed/setprecision";
        if (text == "if" || text.Contains("ветвлен") || text.Contains("услов")) return "if";
        if (text == "for" || text.Contains("цикл for")) return "for";
        if (text == "while" || text.Contains("цикл while")) return "while";
        if (text.Contains("sqrt") || text.Contains("pow")) return "sqrt/pow";
        if (text.Contains("abs") || text.Contains("модул")) return "abs";
        return null;
    }

    private static readonly HashSet<string> _knownPedagogicalConcepts = new(StringComparer.OrdinalIgnoreCase)
    {
        "cout", "cin", "variables", "program-structure", "printf/scanf",
        "if", "for", "while", "getline", "string", "fixed/setprecision",
        "sqrt/pow", "abs",
    };

    private static bool ShouldFlagConcept(string concept, string? focus)
    {
        // Known pedagogical concepts are always flagged — they represent real prerequisite gaps
        if (_knownPedagogicalConcepts.Contains(concept))
            return true;
        // Unknown concepts (regex-extracted function names, etc.) require focus match
        if (!string.IsNullOrWhiteSpace(focus) && !MatchesFocus(concept, focus))
            return false;
        return true;
    }

    private static bool MatchesFocus(AiFoundryCourseAuditFindingDto finding, string? focus)
        => MatchesFocus($"{finding.Concept} {finding.Reason} {finding.AfterAssignmentTitle} {finding.BeforeAssignmentTitle}", focus);

    private static bool MatchesFocus(string? text, string? focus)
    {
        if (string.IsNullOrWhiteSpace(focus))
            return true;

        var tokens = ExtractFocusTokens(focus);
        if (tokens.Count == 0)
            return true;

        var hay = (text ?? string.Empty).ToLowerInvariant();
        return tokens.Any(token => hay.Contains(token));
    }

    private static bool FocusCompatible(string? existingFocus, string? requestedFocus)
    {
        if (string.IsNullOrWhiteSpace(requestedFocus))
            return true;
        if (string.IsNullOrWhiteSpace(existingFocus))
            return false;

        var requestedTokens = ExtractFocusTokens(requestedFocus);
        if (requestedTokens.Count == 0)
            return true;

        var hay = existingFocus.ToLowerInvariant();
        return requestedTokens.Any(token => hay.Contains(token));
    }

    private static List<string> ExtractFocusTokens(string? focus)
    {
        if (string.IsNullOrWhiteSpace(focus))
            return new List<string>();

        var tokens = focus.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => x.ToLowerInvariant())
            .Where(x => x.Length >= 4)
            .ToList();
        if (tokens.Count == 0)
            return new List<string>();

        var generic = new[] { "курс", "курса", "допил", "пробел", "задан", "мостик", "подвод", "новая", "функц", "посмотри", "изучи", "план", "найди", "ещё", "косяк" };
        return tokens.All(token => generic.Any(g => token.Contains(g))) ? new List<string>() : tokens;
    }

    private static bool IsDiagnosticGapAuditIntent(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var hay = text.ToLowerInvariant();
        var asksAudit = new[] { "косяк", "косяки", "пробел", "пробелы", "найди", "найти", "посмотри", "проверь", "аудит", "слишком рано", "до объясн", "прежде чем", "ещё такие", "опубликовал", "опубликованные" }
            .Any(x => hay.Contains(x, StringComparison.Ordinal));
        var mentionsPedagogy = new[] { "переменн", "cout", "cin", "ввод", "вывод", "include", "namespace", "main", "синтакс", "объясн", "подвод", "лесенк" }
            .Any(x => hay.Contains(x, StringComparison.Ordinal));
        return asksAudit && mentionsPedagogy;
    }

    private static bool IsExplicitBridgePlanDisplayIntent(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var hay = text.ToLowerInvariant();
        return (hay.Contains("план") || hay.Contains("bridge-plan") || hay.Contains("мостик"))
            && (hay.Contains("покажи") || hay.Contains("показать") || hay.Contains("уточни") || hay.Contains("исправь") || hay.Contains("перескажи"));
    }

    private static bool FocusWantsSyntaxBasics(string? focus)
    {
        if (string.IsNullOrWhiteSpace(focus))
            return false;

        var hay = focus.ToLowerInvariant();
        return new[] { "переменн", "cout", "cin", "ввод", "вывод", "include", "namespace", "main", "синтакс", "самая базовая программа" }
            .Any(x => hay.Contains(x, StringComparison.Ordinal));
    }

    private static int SuggestTaskCountForConcept(string concept)
        => concept.ToLowerInvariant() switch
        {
            "getline" => 2,
            "string" => 2,
            "printf/scanf" => 2,
            "fixed/setprecision" => 2,
            "sqrt/pow" => 2,
            "variables" => 2,
            "program-structure" => 2,
            "cin" => 2,
            "if" => 3,
            "for" => 6,
            "while" => 6,
            _ => 1,
        };

    private static int SuggestDifficultyForConcept(string concept, int currentDifficulty)
        => Math.Max(1, Math.Min(3, concept.Equals("getline", StringComparison.OrdinalIgnoreCase) || concept.Equals("fixed/setprecision", StringComparison.OrdinalIgnoreCase) ? Math.Max(1, currentDifficulty - 1) : currentDifficulty));

    private static string BuildFindingReason(string concept, string? beforeTitle)
        => concept switch
        {
            "cout" => $"в курсе требуется вывод через cout раньше, чем ученику мягко объясняют самый базовый вывод{FormatBeforeTitle(beforeTitle)}",
            "cin" => $"в курсе появляется ввод через cin раньше, чем объяснены переменная и базовый синтаксис ввода{FormatBeforeTitle(beforeTitle)}",
            "variables" => $"в курсе требуется создать или использовать переменную до явного объяснения, что такое переменная{FormatBeforeTitle(beforeTitle)}",
            "program-structure" => $"в курсе появляется каркас программы (#include / using namespace std / main) без мягкого предварительного объяснения{FormatBeforeTitle(beforeTitle)}",
            "printf/scanf" => $"в курсе появляется printf/scanf без отдельной подводки{FormatBeforeTitle(beforeTitle)}",
            "getline" => $"в курсе появляется чтение строки с пробелами / getline без мостика{FormatBeforeTitle(beforeTitle)}",
            "fixed/setprecision" => $"в курсе появляется точный форматированный вывод без вводящих упражнений{FormatBeforeTitle(beforeTitle)}",
            "string" => $"в курсе появляется работа со строками без постепенного введения{FormatBeforeTitle(beforeTitle)}",
            "if" => $"в курсе начинается работа с условиями и ветвлением без достаточной подводки{FormatBeforeTitle(beforeTitle)}",
            "for" => $"в курсе начинается тема циклов for без мягкой лестницы перед повторяющимися действиями{FormatBeforeTitle(beforeTitle)}",
            "while" => $"в курсе начинается тема циклов while без мягкой лестницы перед повторяющимися действиями{FormatBeforeTitle(beforeTitle)}",
            _ => $"в курсе резко появляется новая функция или конструкция «{concept}»{FormatBeforeTitle(beforeTitle)}",
        };

    private static bool MatchesInspectionQuery(CourseAuditAssignmentSnapshot item, string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return true;

        var hay = ($"{item.Title} {ExtractPlainTextFromRichDescription(item.Description)}").ToLowerInvariant();
        var tokens = ExtractInspectionTokens(query);
        if (tokens.Count == 0)
            return hay.Contains(query.Trim().ToLowerInvariant());
        return tokens.Any(hay.Contains);
    }

    private static List<string> ExtractInspectionTokens(string query)
    {
        var stop = new HashSet<string>(new[]
        {
            "курс", "курса", "для", "перед", "после", "теперь", "нужен", "нужна", "нужно", "мостики", "мостиков", "задания", "заданий", "абсолютных", "новичков", "стиль", "максимально", "простой", "пошаговый", "якорь", "конкретного", "только", "место", "вставки", "assignmentid", "покажи", "нужном", "формате", "варианта", "вариантов"
        }, StringComparer.OrdinalIgnoreCase);
        var tokens = Regex.Matches(query.ToLowerInvariant(), @"[a-zа-я0-9_+#-]{3,}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            .Select(x => x.Value)
            .Where(x => !stop.Contains(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (tokens.Any(x => x.Contains("цикл")))
            tokens.AddRange(new[] { "цикл", "for", "while", "повтор" });
        if (tokens.Any(x => x == "if" || x.Contains("услов") || x.Contains("ветв")))
            tokens.AddRange(new[] { "if", "услов", "ветв" });
        return tokens.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static List<int> BuildInspectionLandmarkIndexes(int count)
    {
        var indexes = new SortedSet<int>();
        if (count <= 0)
            return indexes.ToList();
        indexes.Add(0);
        indexes.Add(Math.Max(0, count - 1));
        for (var i = 0; i < count; i += 3)
            indexes.Add(i);
        for (var i = 1; i < count; i++)
        {
            if (i % 10 == 0)
                indexes.Add(i);
        }
        return indexes.ToList();
    }

    private static string BuildDescriptionExcerpt(string? raw)
    {
        var normalized = ShortenSingleLine(ExtractPlainTextFromRichDescription(raw), 220);
        return string.IsNullOrWhiteSpace(normalized) ? "без описания" : normalized;
    }

    private static Guid? ExtractGuidFromText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var match = Regex.Match(text, @"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b", RegexOptions.CultureInvariant);
        return match.Success && Guid.TryParse(match.Value, out var value) ? value : null;
    }

    private static string FormatBeforeTitle(string? beforeTitle)
        => string.IsNullOrWhiteSpace(beforeTitle) ? string.Empty : $", перед заданием «{beforeTitle}»";

    private static string? ReadString(JsonObject args, string propertyName)
        => args[propertyName]?.ToString()?.Trim();

    private static string? ReadString(JsonElement args, string propertyName)
    {
        if (!args.TryGetProperty(propertyName, out var value))
            return null;
        if (value.ValueKind == JsonValueKind.String)
            return value.GetString()?.Trim();
        return value.ToString()?.Trim();
    }

    private static string Truncate(string? text, int maxLen)
    {
        if (string.IsNullOrEmpty(text)) return "—";
        return text.Length <= maxLen ? text : text[..maxLen] + "…";
    }

    private static int? ReadInt(JsonObject args, string propertyName)
    {
        if (args[propertyName] == null)
            return null;
        try
        {
            return args[propertyName]!.GetValue<int>();
        }
        catch
        {
            if (int.TryParse(args[propertyName]?.ToString(), out var value))
                return value;
            return null;
        }
    }

    private static int? ReadInt(JsonElement args, string propertyName)
    {
        if (!args.TryGetProperty(propertyName, out var value))
            return null;
        try
        {
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
                return number;
            if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out var parsed))
                return parsed;
            if (int.TryParse(value.ToString(), out var fallback))
                return fallback;
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static bool? ReadBool(JsonObject args, string propertyName)
    {
        if (args[propertyName] == null)
            return null;
        try
        {
            return args[propertyName]!.GetValue<bool>();
        }
        catch
        {
            if (bool.TryParse(args[propertyName]?.ToString(), out var value))
                return value;
            return null;
        }
    }

    private static List<int> ReadIndexes(JsonObject args, params string[] keys)
    {
        var indexes = new List<int>();
        foreach (var key in keys)
        {
            if (string.IsNullOrWhiteSpace(key))
                continue;
            if (args[key] is JsonArray arr)
            {
                foreach (var node in arr)
                {
                    if (node == null)
                        continue;
                    if (int.TryParse(node.ToString(), out var idx))
                        indexes.Add(idx);
                }
                continue;
            }
            var single = ReadInt(args, key);
            if (single.HasValue)
                indexes.Add(single.Value);
        }
        return indexes.Distinct().ToList();
    }

    private static Guid? ReadGuid(JsonObject args, string propertyName)
    {
        var raw = ReadString(args, propertyName);
        return Guid.TryParse(raw, out var value) ? value : null;
    }

    private static Guid? ReadGuid(JsonElement args, string propertyName)
    {
        var raw = ReadString(args, propertyName);
        return Guid.TryParse(raw, out var value) ? value : null;
    }
}
