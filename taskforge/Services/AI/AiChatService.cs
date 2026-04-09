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

                    var batch = await _jobs.QueueGenerateAssignmentBatchAsync(new AiGenerateAssignmentBatchRequestDto
                    {
                        CourseId = courseId.Value,
                        AssignmentType = ReadString(args, "assignmentType") ?? "code-test",
                        Prompt = ReadString(args, "prompt") ?? BuildFallbackPrompt(messages),
                        Count = Math.Clamp(ReadInt(args, "count") ?? 5, 1, 50),
                        Mode = ReadString(args, "mode") ?? "topic-pack",
                        Difficulty = Math.Clamp(ReadInt(args, "difficulty") ?? 2, 1, 5),
                        Notes = ReadString(args, "notes"),
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
                    description = "Создать batch из нескольких заданий через существующий Foundry pipeline.",
                    requiredArguments = new[] { "courseId", "prompt" },
                    optionalArguments = new[] { "assignmentType", "count", "difficulty", "mode", "notes", "priority" },
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
                    optionalArguments = new[] { "courseId", "titleOverride", "difficulty", "rating", "tags", "sort", "forceWithoutPassedSelfCheck" },
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

    private static Guid? ReadGuid(JsonObject args, string propertyName)
    {
        var raw = ReadString(args, propertyName);
        return Guid.TryParse(raw, out var value) ? value : null;
    }
}
