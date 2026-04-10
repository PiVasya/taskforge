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

    private readonly ApplicationDbContext _db;
    private readonly IAiJobService _jobs;
    private readonly ILogger<AiChatService> _log;

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
        var parsedMessages = DeserializeMessages(session.MessagesJson);
        var memory = BuildMemory(parsedMessages, session.PlanJson);
        var courseMap = await LoadCourseTitleMapAsync(session.CourseId.HasValue ? new[] { session.CourseId.Value } : Array.Empty<Guid>(), ct);
        return MapSession(session, courseMap, parsedMessages, memory);
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
            PlanJson = SerializeMemory(new AiFoundryChatMemoryDto { Summary = "Новая сессия. Пока без сообщений." }),
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
        };

        _db.AiFoundryChatSessions.Add(session);
        await _db.SaveChangesAsync(ct);

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
        session.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        var courseMap = await LoadCourseTitleMapAsync(session.CourseId.HasValue ? new[] { session.CourseId.Value } : Array.Empty<Guid>(), ct);
        return MapSession(session, courseMap);
    }

    public async Task<bool> DeleteSessionAsync(Guid userId, Guid sessionId, CancellationToken ct = default)
    {
        var session = await _db.AiFoundryChatSessions.FirstOrDefaultAsync(x => x.Id == sessionId && x.CreatedByUserId == userId, ct);
        if (session == null)
            return false;

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

        var messages = DeserializeMessages(session.MessagesJson);
        var pendingAssistant = messages.LastOrDefault(IsPendingAssistant);

        if (pendingAssistant != null)
        {
            var pendingCourseMap = await LoadCourseTitleMapAsync(session.CourseId.HasValue ? new[] { session.CourseId.Value } : Array.Empty<Guid>(), ct);
            return new AiFoundryChatSendMessageResponseDto
            {
                Pending = true,
                PendingJobId = pendingAssistant.PendingJobId,
                Session = MapSession(session, pendingCourseMap, messages, BuildMemory(messages, session.PlanJson)),
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

        session.PlanJson = SerializeMemory(BuildMemory(messages, session.PlanJson));
        var payload = await BuildChatPayloadAsync(session, messages, ct);
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
            Session = MapSession(session, resultCourseMap, messages, BuildMemory(messages, session.PlanJson)),
        };
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

        var messages = DeserializeMessages(session.MessagesJson);
        if (messages.LastOrDefault(IsPendingAssistant) != null)
        {
            var pendingCourseMap = await LoadCourseTitleMapAsync(session.CourseId.HasValue ? new[] { session.CourseId.Value } : Array.Empty<Guid>(), ct);
            return new AiFoundryChatSendMessageResponseDto
            {
                Pending = true,
                PendingJobId = messages.LastOrDefault(IsPendingAssistant)?.PendingJobId,
                Session = MapSession(session, pendingCourseMap, messages, BuildMemory(messages, session.PlanJson)),
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

        var result = await ExecuteToolCallAsync(session, messages, toolCall, userId, userDisplayName, ct);
        var toolResults = result == null ? new List<AiFoundryChatToolResultDto>() : new List<AiFoundryChatToolResultDto> { result };

        messages.Add(new AiFoundryChatMessageDto
        {
            Id = Guid.NewGuid(),
            Role = "assistant",
            Content = BuildAssistantContent("Подтверждение получено. Выполняю действие.", toolResults),
            CreatedAtUtc = DateTime.UtcNow,
            Status = result?.Status == "failed" ? "failed" : "done",
            ToolCalls = new List<AiFoundryChatToolCallDto> { toolCall },
            ToolCall = toolCall,
            ToolResults = toolResults,
            ToolResult = result,
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
        var toolResults = await ExecuteToolCallsAsync(session, messages, toolCalls, job.CreatedByUserId ?? session.CreatedByUserId, job.CreatedByDisplayName, ct);

        assistantMessage.Status = "done";
        assistantMessage.PendingJobId = null;
        assistantMessage.ToolCalls = toolCalls.ToList();
        assistantMessage.ToolCall = assistantMessage.ToolCalls.FirstOrDefault();
        assistantMessage.ToolResults = toolResults.ToList();
        assistantMessage.ToolResult = assistantMessage.ToolResults.FirstOrDefault();
        assistantMessage.Content = BuildAssistantContent(assistantText, toolResults);

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

    private async Task<IReadOnlyList<AiFoundryChatToolResultDto>> ExecuteToolCallsAsync(
        AiFoundryChatSession session,
        List<AiFoundryChatMessageDto> messages,
        IReadOnlyList<AiFoundryChatToolCallDto> toolCalls,
        Guid? createdByUserId,
        string? createdByDisplayName,
        CancellationToken ct)
    {
        var results = new List<AiFoundryChatToolResultDto>();
        foreach (var toolCall in toolCalls.Take(3))
        {
            var result = await ExecuteToolCallAsync(session, messages, toolCall, createdByUserId, createdByDisplayName, ct);
            if (result != null)
                results.Add(result);
        }

        return results;
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

        try
        {
            switch (action)
            {
                case "queue_generate_batch":
                {
                    var courseId = ReadGuid(args, "courseId") ?? session.CourseId;
                    if (!courseId.HasValue)
                        return FailTool("Нужно выбрать courseId, прежде чем создавать batch.");

                    var prompt = ReadString(args, "prompt") ?? BuildFallbackPrompt(messages);
                    var count = Math.Clamp(ReadInt(args, "count") ?? 5, 1, 50);
                    var difficulty = Math.Clamp(ReadInt(args, "difficulty") ?? 2, 1, 5);
                    var memory = BuildMemory(messages, session.PlanJson);
                    var structuredContextJson = BuildStructuredBatchContextJson(session, messages, memory, courseId.Value, prompt, args, "topic-pack", count, difficulty);

                    var batch = await _jobs.QueueGenerateAssignmentBatchAsync(new AiGenerateAssignmentBatchRequestDto
                    {
                        CourseId = courseId.Value,
                        AssignmentType = ReadString(args, "assignmentType") ?? "code-test",
                        Prompt = prompt,
                        Count = count,
                        Mode = ReadString(args, "mode") ?? "topic-pack",
                        Difficulty = difficulty,
                        Notes = ReadString(args, "notes"),
                        StructuredContextJson = structuredContextJson,
                        Priority = Math.Clamp(ReadInt(args, "priority") ?? 20, 1, 100),
                    }, createdByUserId, createdByDisplayName, ct);

                    return new AiFoundryChatToolResultDto
                    {
                        Status = "done",
                        Summary = $"Создал AI batch на {batch.RequestedCount} заданий. Статус: {batch.Status}.",
                        NavigateTo = "/admin/ai",
                        BatchId = batch.Id,
                        CourseId = batch.CourseId,
                    };
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
                    var audit = memory.LastCourseAudit;
                    if (audit == null || audit.CourseId != courseId.Value || audit.Findings.Count == 0)
                        return FailTool("Сначала запусти analyze_course_progression, чтобы я увидела пробелы курса и точки вставки.");

                    var selectedFindings = SelectAuditFindings(audit, args);
                    if (selectedFindings.Count == 0)
                        return FailTool("Не удалось выбрать findings для плана мостиков. Проверь findingIndexes или сначала обнови аудит курса.");

                    var bridgePlan = BuildBridgePlan(audit, memory.LastCourseInspection, selectedFindings, ReadString(args, "focus"));
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
                        return FailTool("Нужно выбрать courseId, прежде чем создавать bridge-batch.");

                    var memory = DeserializeMemory(session.PlanJson);
                    var audit = memory.LastCourseAudit;
                    if (audit == null || audit.CourseId != courseId.Value || audit.Findings.Count == 0)
                        return FailTool("Сначала запусти analyze_course_progression, чтобы я увидела пробелы курса и точки вставки.");

                    var bridgePlan = memory.LastBridgePlan != null && memory.LastBridgePlan.CourseId == courseId.Value
                        ? memory.LastBridgePlan
                        : BuildBridgePlan(audit, memory.LastCourseInspection, SelectAuditFindings(audit, args), ReadString(args, "focus"));
                    if (bridgePlan == null || bridgePlan.Items.Count == 0)
                        return FailTool("Не удалось собрать plan items для bridge-batch. Сначала собери план мостиков или обнови аудит курса.");

                    var selectedItems = SelectBridgePlanItems(bridgePlan, args);
                    if (selectedItems.Count == 0)
                        return FailTool("Не удалось выбрать plan items для bridge-batch. Проверь itemIndexes или сначала обнови план мостиков.");

                    session.PlanJson = SerializeMemory(WithLastBridgePlan(BuildMemory(messages, session.PlanJson), bridgePlan));

                    var requestedCount = Math.Clamp(ReadInt(args, "count") ?? selectedItems.Sum(x => Math.Max(1, x.TaskCount)), 1, 50);
                    var difficulty = Math.Clamp(ReadInt(args, "difficulty") ?? Math.Max(1, Math.Min(3, selectedItems.Max(x => x.Difficulty))), 1, 3);
                    var prompt = BuildBridgeBatchPrompt(audit, bridgePlan, selectedItems, ReadString(args, "prompt"), ReadString(args, "focus"));
                    var notes = BuildBridgeBatchNotes(audit, bridgePlan, selectedItems);

                    var memoryForBatch = BuildMemory(messages, session.PlanJson);
                    var structuredContextJson = BuildStructuredBatchContextJson(session, messages, memoryForBatch, courseId.Value, prompt, args, "bridge-pack", requestedCount, difficulty);

                    var batch = await _jobs.QueueGenerateAssignmentBatchAsync(new AiGenerateAssignmentBatchRequestDto
                    {
                        CourseId = courseId.Value,
                        AssignmentType = "code-test",
                        Prompt = prompt,
                        Count = requestedCount,
                        Mode = "bridge-pack",
                        Difficulty = difficulty,
                        Notes = notes,
                        StructuredContextJson = structuredContextJson,
                        Priority = Math.Clamp(ReadInt(args, "priority") ?? 20, 1, 100),
                    }, createdByUserId, createdByDisplayName, ct);

                    return new AiFoundryChatToolResultDto
                    {
                        Status = "done",
                        Summary = $"Создала bridge-batch на {batch.RequestedCount} задач по плану мостиков. AI будет опираться на afterAssignmentId и title hints из согласованного плана.",
                        NavigateTo = "/admin/ai",
                        BatchId = batch.Id,
                        CourseId = batch.CourseId,
                    };
                }
                case "queue_generate_from_text":
                {
                    var courseId = ReadGuid(args, "courseId") ?? session.CourseId;
                    if (!courseId.HasValue)
                        return FailTool("Нужно выбрать courseId, прежде чем запускать генерацию.");

                    var job = await _jobs.QueueGenerateAssignmentFromTextAsync(new AiGenerateAssignmentFromTextRequestDto
                    {
                        CourseId = courseId.Value,
                        AssignmentType = ReadString(args, "assignmentType") ?? "code-test",
                        Prompt = ReadString(args, "prompt") ?? BuildFallbackPrompt(messages),
                        SourceText = ReadString(args, "sourceText") ?? BuildSourceTextFromRecentAttachments(messages),
                        TitleHint = ReadString(args, "titleHint"),
                        Difficulty = Math.Clamp(ReadInt(args, "difficulty") ?? 2, 1, 5),
                        Count = Math.Clamp(ReadInt(args, "count") ?? 1, 1, 50),
                        Notes = ReadString(args, "notes"),
                        Priority = Math.Clamp(ReadInt(args, "priority") ?? 20, 1, 100),
                        EnableSelfCheck = ReadBool(args, "enableSelfCheck") ?? true,
                    }, createdByUserId, createdByDisplayName, ct);

                    return new AiFoundryChatToolResultDto
                    {
                        Status = "done",
                        Summary = "Поставил в очередь генерацию заданий из текста.",
                        NavigateTo = "/admin/ai",
                        JobId = job.Id,
                        CourseId = courseId,
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
                        Rating = ReadInt(args, "rating"),
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

    private async Task<object> BuildChatPayloadAsync(AiFoundryChatSession session, List<AiFoundryChatMessageDto> messages, CancellationToken ct)
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

        var recentAssignments = await recentAssignmentsQuery
            .OrderByDescending(x => x.UpdatedAt)
            .Take(12)
            .Select(x => new
            {
                id = x.Id,
                courseId = x.CourseId,
                title = x.Title,
                type = x.Type,
                difficulty = x.Difficulty,
                rating = x.Rating,
                updatedAtUtc = x.UpdatedAt,
            })
            .ToListAsync(ct);

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
        }).ToList();

        return new
        {
            requestType = AiFoundryJobTypes.ChatTurn,
            sessionId = session.Id,
            sessionTitle = session.Title,
            courseId = session.CourseId,
            selectedCourse,
            memory,
            conversation,
            recentAttachments = attachmentDigest,
            recentAssignments,
            recentDrafts,
            recentBatches,
            recentJobs,
            recentUsers,
            recentAttempts,
            availableCourses = courses,
            availableActions = new object[]
            {
                new
                {
                    name = "queue_generate_batch",
                    description = "Создать batch из нескольких заданий через Foundry pipeline с опорой на память чата, аудит курса, стиль названий и педагогические подсказки вроде «первоклассники» или «нужны пошаговые путеводители».",
                    requiredArguments = new[] { "courseId", "prompt" },
                    optionalArguments = new[] { "assignmentType", "count", "difficulty", "mode", "notes", "priority" },
                },
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
                    optionalArguments = new[] { "focus", "findingIndexes" },
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
                    description = "Продолжить агента по памяти и текущему состоянию: выбрать следующий логичный шаг между аудитом, просмотром заданий, планом мостиков и bridge-batch.",
                    requiredArguments = new[] { "courseId" },
                    optionalArguments = new[] { "focus", "priority" },
                },
                new
                {
                    name = "queue_generate_bridge_batch",
                    description = "На основе последнего плана мостиков создать batch мостиковых задач перед резким вводом новых функций/тем, сохранив afterAssignmentId, стиль курса и при необходимости guided walkthrough-формат.",
                    requiredArguments = new[] { "courseId" },
                    optionalArguments = new[] { "count", "difficulty", "prompt", "focus", "findingIndexes", "itemIndexes", "priority" },
                },
                new
                {
                    name = "queue_generate_from_text",
                    description = "Запустить генерацию одного или нескольких заданий из текста.",
                    requiredArguments = new[] { "courseId", "prompt" },
                    optionalArguments = new[] { "assignmentType", "sourceText", "count", "difficulty", "titleHint", "notes", "priority", "enableSelfCheck" },
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
                    optionalArguments = new[] { "courseId", "titleOverride", "difficulty", "rating", "tags", "sort", "afterAssignmentId", "forceWithoutPassedSelfCheck" },
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
            },
            defaults = new
            {
                assignmentType = "code-test",
                difficulty = 2,
                count = 5,
                mode = "topic-pack",
            }
        };
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
        var toolCalls = ParseToolCalls(root).ToList();
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
            || Regex.IsMatch(lastUser, @"(batch|пакет|нескольк|много|ещ[её]|задач)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

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
                Name = "queue_generate_batch",
                Reason = "Синтезировано на backend: worker вернул batch-план без actions, поэтому чат восстановил ожидаемое действие.",
                ArgumentsJson = args.ToJsonString(),
            },
            $"Поняла. Запускаю batch на {finalCount} задач по текущему контексту курса.");
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
                "queue_generate_batch" => "Запускаю batch по текущему контексту.",
                "analyze_course_progression" => "Открываю курс и собираю аудит по пробелам и резким вводам новых тем.",
                "inspect_course_assignments" => "Открываю конкретные задания курса, чтобы сверить стиль и последовательность.",
                "prepare_bridge_plan" => "Собираю подробный план вставок и точек afterAssignmentId по курсу.",
                "show_bridge_plan" => "Показываю текущий план мостиков без пересборки.",
                "revise_bridge_plan" => "Точечно правлю уже собранный план мостиков по твоим замечаниям.",
                "advance_agent_stage" => "Продолжаю агента по памяти и выбираю следующий логичный шаг без повторного объяснения контекста.",
                "queue_generate_bridge_batch" => "Запускаю bridge-batch по последнему плану мостиков.",
                "queue_generate_from_text" => "Запускаю генерацию из текста.",
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

    private static string BuildAssistantContent(string? assistantText, IReadOnlyList<AiFoundryChatToolResultDto>? toolResults)
    {
        var text = string.IsNullOrWhiteSpace(assistantText)
            ? "AI завершила обработку, но не вернула текстовый комментарий. Покажу только выполненные действия и результаты."
            : assistantText.Trim();

        var summaries = (toolResults ?? Array.Empty<AiFoundryChatToolResultDto>())
            .Select(x => x.Summary?.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (summaries.Count > 0)
        {
            var appendix = string.Join("\n", summaries.Select(x => $"• {x}"));
            if (!string.IsNullOrWhiteSpace(appendix) && !text.Contains(appendix, StringComparison.OrdinalIgnoreCase))
                text = $"{text}\n\n{appendix}";
        }

        return text;
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
            learnerProfile,
            pedagogy = new
            {
                preferGuidedWalkthroughs = learnerProfile["preferGuidedWalkthroughs"],
                requireSectionIntroGuides = learnerProfile["requireSectionIntroGuides"],
                explainLikeChild = learnerProfile["explainLikeChild"],
                tone = learnerProfile["tone"],
                vocabularyLevel = learnerProfile["vocabularyLevel"],
                maxNewConceptsPerTask = learnerProfile["maxNewConceptsPerTask"],
                requireConcreteExamples = true,
                preferTinySteps = true,
                preferActionVerbs = true,
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
                    suggestedDifficulty = x.SuggestedDifficulty,
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

    private static AiFoundryAgentStateDto BuildChatAgentState(
        AiFoundryChatMemoryDto previous,
        IReadOnlyList<string> recentGoals,
        IReadOnlyList<string> recentActions,
        string? nextAgentStep)
    {
        var intentSummary = recentGoals.Count > 0
            ? ShortenSingleLine(recentGoals[^1], 220)
            : ShortenSingleLine(previous.AgentState?.UserIntentSummary, 220);
        var learnerProfile = BuildLearnerProfileSnapshot(string.Join(" ", recentGoals), intentSummary ?? string.Empty, null);
        var constraints = BuildGenerationConstraintsSnapshot(string.Join(" ", recentGoals), intentSummary ?? string.Empty, null);
        var styleHints = new List<string>();
        if (previous.LastCourseAudit != null)
            styleHints.AddRange(previous.LastCourseAudit.StyleHints ?? new List<string>());
        if (previous.LastBridgePlan != null)
            styleHints.AddRange(previous.LastBridgePlan.StyleHints ?? new List<string>());
        styleHints = styleHints
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => ShortenSingleLine(x, 120))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToList();

        var placementCandidates = new List<AiFoundryAgentPlacementCandidateDto>();
        if (previous.LastBridgePlan != null)
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
        else if (previous.LastCourseAudit != null)
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
                Difficulty = x.SuggestedDifficulty,
                TitleHint = BuildBridgeTitleHint(x, previous.LastCourseAudit?.TitleExamples ?? new List<string>()),
            }));
        }

        var firstPlacement = placementCandidates.FirstOrDefault(x => x.AfterAssignmentId.HasValue);
        var workflowKind = "conversation";
        var currentStage = "idle";
        if (previous.LastBridgePlan != null)
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
            workflowKind = "batch-generation";
            currentStage = "batch-queued";
        }

        return new AiFoundryAgentStateDto
        {
            WorkflowKind = workflowKind,
            CurrentStage = currentStage,
            UserIntentSummary = intentSummary ?? string.Empty,
            LearnerAudience = Convert.ToString(learnerProfile["audience"]) ?? "general",
            PedagogyMode = Convert.ToBoolean(learnerProfile["preferGuidedWalkthroughs"]) || Convert.ToBoolean(learnerProfile["explainLikeChild"]) ? "guided-simple" : "standard",
            NextSuggestedAction = ShortenSingleLine(nextAgentStep, 120),
            PlacementAfterAssignmentId = firstPlacement?.AfterAssignmentId,
            PlacementAfterAssignmentTitle = firstPlacement?.AfterAssignmentTitle,
            HasCourseAudit = previous.LastCourseAudit != null,
            HasCourseInspection = previous.LastCourseInspection != null,
            HasBridgePlan = previous.LastBridgePlan != null,
            ReadyForGeneration = previous.LastBridgePlan != null && (string.Equals(previous.LastBridgePlan.Status, "confirmed", StringComparison.OrdinalIgnoreCase) || previous.LastBridgePlan.Items.Any(x => x.Confirmed && !x.Rejected)),
            ActiveGoals = recentGoals.Take(4).ToList(),
            ActiveConstraints = ((constraints["mustStayBeforeConcepts"] as List<string>) ?? new List<string>())
                .Concat((constraints["avoidConcepts"] as List<string>) ?? new List<string>())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(6)
                .ToList(),
            StyleHints = styleHints,
            PlacementCandidates = placementCandidates.Take(6).ToList(),
        };
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

    private static AiFoundryChatMemoryDto BuildMemory(List<AiFoundryChatMessageDto> messages, string? existingMemoryJson = null)
    {
        var previous = DeserializeMemory(existingMemoryJson);
        var userMessages = messages
            .Where(x => string.Equals(x.Role, "user", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(x.Content))
            .ToList();
        var assistantMessages = messages
            .Where(x => string.Equals(x.Role, "assistant", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(x.Content) && !string.Equals(x.Status, "processing", StringComparison.OrdinalIgnoreCase))
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

        var facts = new List<string>();
        if (recentGoals.Count > 0)
            facts.Add($"Последняя цель пользователя: {recentGoals[^1]}");
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
        if (recentActions.Count > 0)
            summaryParts.Add($"Ранее уже использовали действия: {string.Join(", ", recentActions.Take(3))}.");
        if (recentEntities.Count > 0)
            summaryParts.Add($"Последние сущности: {string.Join(", ", recentEntities.Take(3))}.");
        if (!string.IsNullOrWhiteSpace(lastAssistantOutcome))
            summaryParts.Add($"Последний ответ AI: {lastAssistantOutcome}.");

        var summary = string.Join(" ", summaryParts).Trim();
        if (string.IsNullOrWhiteSpace(summary))
            summary = !string.IsNullOrWhiteSpace(previous.Summary) ? previous.Summary : "Пока это пустая сессия без накопленной памяти.";

        if (previous.LastCourseAudit != null && facts.Count < 6)
            facts.Add($"Последний аудит курса: {ShortenSingleLine(previous.LastCourseAudit.Summary, 140)}");
        if (previous.LastCourseAudit != null)
            summary = string.Join(" ", new[] { summary, $"Последний аудит курса: {ShortenSingleLine(previous.LastCourseAudit.Summary, 120)}." }.Where(x => !string.IsNullOrWhiteSpace(x))).Trim();
        if (previous.LastCourseInspection != null && facts.Count < 6)
            facts.Add($"Последний просмотр заданий: {ShortenSingleLine(previous.LastCourseInspection.Summary, 140)}");
        if (previous.LastCourseInspection != null)
            summary = string.Join(" ", new[] { summary, $"Последний просмотр заданий: {ShortenSingleLine(previous.LastCourseInspection.Summary, 120)}." }.Where(x => !string.IsNullOrWhiteSpace(x))).Trim();
        if (previous.LastBridgePlan != null && facts.Count < 6)
            facts.Add($"Последний план мостиков: {ShortenSingleLine(previous.LastBridgePlan.Summary, 140)}");
        if (previous.LastBridgePlan != null)
            summary = string.Join(" ", new[] { summary, $"Последний план мостиков: {ShortenSingleLine(previous.LastBridgePlan.Summary, 120)}." }.Where(x => !string.IsNullOrWhiteSpace(x))).Trim();
        var nextAgentStep = SuggestNextAgentStep(previous);
        if (!string.IsNullOrWhiteSpace(nextAgentStep) && facts.Count < 6)
            facts.Add($"Следующий логичный шаг агента: {nextAgentStep}");
        if (!string.IsNullOrWhiteSpace(nextAgentStep))
            summary = string.Join(" ", new[] { summary, $"Следующий логичный шаг агента: {nextAgentStep}." }.Where(x => !string.IsNullOrWhiteSpace(x))).Trim();

        var agentState = BuildChatAgentState(previous, recentGoals, recentActions, nextAgentStep);
        if (!string.IsNullOrWhiteSpace(agentState.CurrentStage) && facts.Count < 6)
            facts.Add($"Стадия агента: {agentState.CurrentStage}");
        if (!string.IsNullOrWhiteSpace(agentState.UserIntentSummary))
            summary = string.Join(" ", new[] { summary, $"Каноническая цель агента: {agentState.UserIntentSummary}." }.Where(x => !string.IsNullOrWhiteSpace(x))).Trim();

        return new AiFoundryChatMemoryDto
        {
            Summary = summary,
            Facts = facts.Take(6).ToList(),
            RecentGoals = recentGoals,
            RecentFiles = recentFiles,
            RecentActions = recentActions,
            MessageCount = messages.Count,
            LastUserMessageAtUtc = userMessages.LastOrDefault()?.CreatedAtUtc,
            LastAssistantMessageAtUtc = assistantMessages.LastOrDefault()?.CreatedAtUtc,
            LastCourseAudit = previous.LastCourseAudit,
            LastCourseInspection = previous.LastCourseInspection,
            LastBridgePlan = previous.LastBridgePlan,
            AgentState = agentState,
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
            "queue_generate_batch" => "создание batch",
            "analyze_course_progression" => "аудит курса",
            "inspect_course_assignments" => "просмотр заданий курса",
            "prepare_bridge_plan" => "план мостиков по курсу",
            "show_bridge_plan" => "показ плана мостиков",
            "revise_bridge_plan" => "точечная правка плана мостиков",
            "advance_agent_stage" => "автопродолжение агента",
            "queue_generate_bridge_batch" => "bridge-batch по плану мостиков",
            "queue_generate_from_text" => "генерация из текста",
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
            .Take(8)
            .ToList();
        var styleHints = DetectTitleStyleHints(ordered, titleExamples);
        var focusText = (focus ?? string.Empty).Trim();

        for (var i = 0; i < ordered.Count; i++)
        {
            var current = ordered[i];
            var concepts = ExtractCourseConcepts(current.Title + "\n" + current.Description);
            foreach (var concept in concepts)
            {
                if (!seenConcepts.Add(concept))
                    continue;
                if (!ShouldFlagConcept(concept, focusText))
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

        findings = findings
            .Where(x => MatchesFocus(x, focusText))
            .Take(6)
            .ToList();

        return new AiFoundryCourseAuditDto
        {
            CourseId = course.Id,
            CourseTitle = course.Title,
            Focus = string.IsNullOrWhiteSpace(focusText) ? null : focusText,
            GeneratedAtUtc = DateTime.UtcNow,
            Summary = findings.Count == 0
                ? "Явных резких вводов новых функций/конструкций по быстрым эвристикам не найдено."
                : $"Нашла {findings.Count} точек, где стоит вставить bridge-задачи до резкого ввода новой функции или конструкции.",
            StyleHints = styleHints,
            TitleExamples = titleExamples,
            Findings = findings,
        };
    }

    private async Task<AiFoundryCourseInspectionDto?> InspectCourseAssignmentsAsync(Guid courseId, string? query, Guid? aroundAssignmentId, int? window, int? limitAssignments, CancellationToken ct)
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
        var radius = Math.Clamp(window ?? 1, 0, 3);
        var maxItems = Math.Clamp(limitAssignments ?? 8, 1, 12);

        if (aroundAssignmentId.HasValue)
        {
            var index = ordered.FindIndex(x => x.Id == aroundAssignmentId.Value);
            if (index >= 0)
            {
                var from = Math.Max(0, index - radius);
                var take = Math.Min(ordered.Count - from, radius * 2 + 1);
                selected.AddRange(ordered.Skip(from).Take(take));
            }
        }

        if (selected.Count == 0 && !string.IsNullOrWhiteSpace(normalizedQuery))
        {
            var matches = ordered
                .Select((item, idx) => new { item, idx })
                .Where(x => MatchesInspectionQuery(x.item, normalizedQuery))
                .ToList();
            var indexes = new SortedSet<int>();
            foreach (var match in matches)
            {
                var from = Math.Max(0, match.idx - radius);
                var to = Math.Min(ordered.Count - 1, match.idx + radius);
                for (var i = from; i <= to; i++)
                    indexes.Add(i);
            }
            selected.AddRange(indexes.Take(maxItems).Select(i => ordered[i]));
        }

        if (selected.Count == 0)
            selected.AddRange(ordered.Take(maxItems));

        var assignments = selected
            .DistinctBy(x => x.Id)
            .OrderBy(x => x.Sort)
            .Take(maxItems)
            .Select(x => new AiFoundryCourseInspectionAssignmentDto
            {
                Id = x.Id,
                Sort = x.Sort,
                Difficulty = x.Difficulty,
                Title = x.Title,
                DescriptionExcerpt = BuildDescriptionExcerpt(x.Description),
            })
            .ToList();

        return new AiFoundryCourseInspectionDto
        {
            CourseId = course.Id,
            CourseTitle = course.Title,
            Query = string.IsNullOrWhiteSpace(normalizedQuery) ? null : normalizedQuery,
            AroundAssignmentId = aroundAssignmentId,
            GeneratedAtUtc = DateTime.UtcNow,
            Summary = assignments.Count == 0
                ? "Подходящих заданий для просмотра не найдено."
                : $"Открыла {assignments.Count} заданий курса, чтобы сверить стиль, соседние темы и место вставки новых bridge-задач.",
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

        return ordered;
    }

    private AiFoundryChatToolCallDto? BuildNextAgentToolCall(Guid courseId, AiFoundryChatSession session, List<AiFoundryChatMessageDto> messages, JsonObject args)
    {
        var memory = DeserializeMemory(session.PlanJson);
        var focus = ReadString(args, "focus") ?? _chatFallbackFocus(messages);
        if (memory.LastCourseAudit == null || memory.LastCourseAudit.CourseId != courseId || memory.LastCourseAudit.Findings.Count == 0)
        {
            return new AiFoundryChatToolCallDto
            {
                Name = "analyze_course_progression",
                Reason = "В памяти ещё нет актуального аудита курса, поэтому следующий логичный шаг — сначала изучить курс и найти пробелы.",
                ArgumentsJson = JsonSerializer.Serialize(new { courseId, focus }, JsonOptions),
            };
        }

        if (memory.LastCourseInspection == null || memory.LastCourseInspection.CourseId != courseId || memory.LastCourseInspection.Assignments.Count == 0)
        {
            var firstFinding = memory.LastCourseAudit.Findings.FirstOrDefault();
            return new AiFoundryChatToolCallDto
            {
                Name = "inspect_course_assignments",
                Reason = "После аудита полезно открыть реальные задания курса вокруг первой точки вставки, чтобы сверить стиль и последовательность.",
                ArgumentsJson = JsonSerializer.Serialize(new
                {
                    courseId,
                    query = focus,
                    aroundAssignmentId = firstFinding?.AfterAssignmentId,
                    window = 1,
                    limitAssignments = 8,
                }, JsonOptions),
            };
        }

        if (memory.LastBridgePlan == null || memory.LastBridgePlan.CourseId != courseId || memory.LastBridgePlan.Items.Count == 0)
        {
            return new AiFoundryChatToolCallDto
            {
                Name = "prepare_bridge_plan",
                Reason = "Аудит и просмотр заданий уже есть, значит следующий шаг — зафиксировать подробный план мостиков.",
                ArgumentsJson = JsonSerializer.Serialize(new { courseId, focus }, JsonOptions),
            };
        }

        var confirmedItems = memory.LastBridgePlan.Items.Where(x => x.Confirmed && !x.Rejected).Select(x => x.Index).ToList();
        if (string.Equals(memory.LastBridgePlan.Status, "confirmed", StringComparison.OrdinalIgnoreCase) || confirmedItems.Count > 0)
        {
            return new AiFoundryChatToolCallDto
            {
                Name = "queue_generate_bridge_batch",
                Reason = "В плане уже есть подтверждённые точки вставки, поэтому можно переходить к bridge-generation.",
                ArgumentsJson = JsonSerializer.Serialize(new { courseId, focus, itemIndexes = confirmedItems }, JsonOptions),
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
        if (memory.LastCourseAudit == null || memory.LastCourseAudit.Findings.Count == 0)
            return "сначала сделать analyze_course_progression";
        if (memory.LastCourseInspection == null || memory.LastCourseInspection.Assignments.Count == 0)
            return "открыть соседние задания через inspect_course_assignments";
        if (memory.LastBridgePlan == null || memory.LastBridgePlan.Items.Count == 0)
            return "собрать план мостиков через prepare_bridge_plan";
        var confirmed = memory.LastBridgePlan.Items.Count(x => x.Confirmed && !x.Rejected);
        if (string.Equals(memory.LastBridgePlan.Status, "confirmed", StringComparison.OrdinalIgnoreCase) || confirmed > 0)
            return "сгенерировать мостики через queue_generate_bridge_batch";
        return "показать и уточнить план мостиков через show_bridge_plan / revise_bridge_plan";
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
        var sb = new StringBuilder();
        sb.Append($"Изучила курс «{report.CourseTitle}». ");
        if (report.StyleHints.Count > 0)
            sb.Append($"По названиям вижу такой стиль: {string.Join(", ", report.StyleHints.Take(3))}. ");
        if (report.TitleExamples.Count > 0)
            sb.Append($"Хорошие ориентиры по названиям: {string.Join("; ", report.TitleExamples.Take(4))}. ");
        sb.Append(report.Summary);

        if (report.Findings.Count > 0)
        {
            sb.Append("\n\nЧто стоит вставить:");
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
            sb.Append("\n\nЕсли нужно, я могу ещё отдельно открыть соседние задания и показать конкретные названия/формулировки вокруг точек вставки.");
            sb.Append("\nПосле этого я могу собрать подробный план вставок с afterAssignmentId, количеством задач и title hints.");
            sb.Append("\nЕсли план ок, можно следующим сообщением попросить: «собери план мостиков» или «сгенерируй мостики по этому плану».");
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
        string? focus)
    {
        var normalizedFocus = string.IsNullOrWhiteSpace(focus) ? audit.Focus : focus?.Trim();
        var items = findings
            .Where(x => MatchesFocus(x, normalizedFocus))
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
        var selected = revised.Items
            .Where(x => indexes.Count == 0 || indexes.Contains(x.Index) || indexes.Contains(x.Index - 1))
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
            sb.Append("\n\nЕсли хочешь, я могу точечно поправить этот план по замечаниям, подтвердить нужные пункты или сразу сгенерировать bridge-batch только по подтверждённым точкам.");
        }

        var result = sb.ToString().Trim();
        return result.Length <= 2600 ? result : result[..2600];
    }

    private static string BuildBridgeBatchPrompt(
        AiFoundryCourseAuditDto audit,
        AiFoundryBridgePlanDto plan,
        List<AiFoundryBridgePlanItemDto> items,
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
        sb.AppendLine("Работай по согласованному плану вставок, а не придумывай точки размещения с нуля.");
        sb.AppendLine("Используй стиль названий и формулировок текущего курса, избегай абстрактных и слишком общих названий.");
        if (plan.StyleHints.Count > 0)
            sb.AppendLine($"Стиль названий курса: {string.Join("; ", plan.StyleHints.Take(4))}.");
        if (audit.TitleExamples.Count > 0)
            sb.AppendLine($"Примеры живых названий из курса: {string.Join("; ", audit.TitleExamples.Take(6))}.");
        sb.AppendLine("Не используй расплывчатые названия вроде «Форматированный вывод», «Работа со строкой», «Базовый ввод» и подобные. Название должно быть таким же конкретным, как у существующих заданий курса.");
        sb.AppendLine("Для каждого нового задания обязательно выбери placementAfterAssignmentId из plan items ниже.");
        sb.AppendLine("Plan items:");
        foreach (var item in items)
        {
            var examples = item.TitleExamples.Count == 0 ? "-" : string.Join(" | ", item.TitleExamples.Take(4));
            sb.AppendLine($"- item #{item.Index}: afterAssignmentId={item.AfterAssignmentId}; afterTitle={item.AfterAssignmentTitle}; beforeTitle={item.BeforeAssignmentTitle}; concept={item.Concept}; reason={item.Reason}; taskCount={item.TaskCount}; difficulty={item.Difficulty}; titleHint={item.TitleHint}; nearbyTitleExamples={examples}.");
        }
        sb.AppendLine("Не копируй существующие задания дословно. Сгенерируй именно вводящие мостики, а не ещё один общий topic-pack.");
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
            "cout" => "Одно значение и вывод",
            "cin" => "Два значения и действие",
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

    private static HashSet<string> ExtractCourseConcepts(string? raw)
    {
        var text = (raw ?? string.Empty).ToLowerInvariant();
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void add(string key, params string[] needles)
        {
            if (needles.Any(n => text.Contains(n)))
                set.Add(key);
        }
        add("cout", "cout");
        add("cin", "cin");
        add("printf/scanf", "printf", "scanf");
        add("getline", "getline", "строк с пробел", "строки с пробел", "строку с пробел");
        add("string", " string", "строк");
        add("fixed/setprecision", "setprecision", "fixed", "форматированн");
        add("if", " if ", "услов", "ветв");
        add("for", " for ", "цикл for");
        add("while", " while ", "цикл while");
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
        return set;
    }

    private static bool ShouldFlagConcept(string concept, string? focus)
    {
        if (!string.IsNullOrWhiteSpace(focus) && !MatchesFocus(concept, focus))
            return false;
        if (concept.Equals("cout", StringComparison.OrdinalIgnoreCase) || concept.Equals("cin", StringComparison.OrdinalIgnoreCase))
            return false;
        if (concept.Equals("if", StringComparison.OrdinalIgnoreCase) || concept.Equals("for", StringComparison.OrdinalIgnoreCase) || concept.Equals("while", StringComparison.OrdinalIgnoreCase))
            return false;
        return true;
    }

    private static bool MatchesFocus(AiFoundryCourseAuditFindingDto finding, string? focus)
        => MatchesFocus($"{finding.Concept} {finding.Reason} {finding.AfterAssignmentTitle} {finding.BeforeAssignmentTitle}", focus);

    private static bool MatchesFocus(string? text, string? focus)
    {
        if (string.IsNullOrWhiteSpace(focus))
            return true;

        var tokens = focus.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => x.ToLowerInvariant())
            .Where(x => x.Length >= 4)
            .ToList();
        if (tokens.Count == 0)
            return true;

        var generic = new[] { "курс", "курса", "допил", "пробел", "задан", "мостик", "подвод", "новая", "функц", "посмотри", "изучи", "план" };
        if (tokens.All(token => generic.Any(g => token.Contains(g))))
            return true;

        var hay = (text ?? string.Empty).ToLowerInvariant();
        return tokens.Any(token => hay.Contains(token));
    }

    private static int SuggestTaskCountForConcept(string concept)
        => concept.ToLowerInvariant() switch
        {
            "getline" => 2,
            "string" => 2,
            "printf/scanf" => 2,
            "fixed/setprecision" => 2,
            "sqrt/pow" => 2,
            _ => 1,
        };

    private static int SuggestDifficultyForConcept(string concept, int currentDifficulty)
        => Math.Max(1, Math.Min(3, concept.Equals("getline", StringComparison.OrdinalIgnoreCase) || concept.Equals("fixed/setprecision", StringComparison.OrdinalIgnoreCase) ? Math.Max(1, currentDifficulty - 1) : currentDifficulty));

    private static string BuildFindingReason(string concept, string? beforeTitle)
        => concept switch
        {
            "printf/scanf" => $"в курсе появляется printf/scanf без отдельной подводки{FormatBeforeTitle(beforeTitle)}",
            "getline" => $"в курсе появляется чтение строки с пробелами / getline без мостика{FormatBeforeTitle(beforeTitle)}",
            "fixed/setprecision" => $"в курсе появляется точный форматированный вывод без вводящих упражнений{FormatBeforeTitle(beforeTitle)}",
            "string" => $"в курсе появляется работа со строками без постепенного введения{FormatBeforeTitle(beforeTitle)}",
            _ => $"в курсе резко появляется новая функция или конструкция «{concept}»{FormatBeforeTitle(beforeTitle)}",
        };

    private static bool MatchesInspectionQuery(CourseAuditAssignmentSnapshot item, string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return true;

        var hay = ($"{item.Title} {item.Description}").ToLowerInvariant();
        var tokens = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => x.ToLowerInvariant())
            .Where(x => x.Length >= 3)
            .ToList();
        if (tokens.Count == 0)
            return hay.Contains(query.Trim().ToLowerInvariant());
        return tokens.Any(hay.Contains);
    }

    private static string BuildDescriptionExcerpt(string? raw)
    {
        var normalized = ShortenSingleLine((raw ?? string.Empty).Replace("\r", " ").Replace("\n", " "), 140);
        return string.IsNullOrWhiteSpace(normalized) ? "без описания" : normalized;
    }

    private static string FormatBeforeTitle(string? beforeTitle)
        => string.IsNullOrWhiteSpace(beforeTitle) ? string.Empty : $", перед заданием «{beforeTitle}»";

    private static string? ReadString(JsonObject args, string propertyName)
        => args[propertyName]?.ToString()?.Trim();

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
}
