using System.ComponentModel.DataAnnotations;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using taskforge.Constants;
using taskforge.Data;
using taskforge.Data.Models.DTO.Agent;
using taskforge.Data.Models.Entities;
using taskforge.Hubs;
using taskforge.Services.Interfaces;
using taskforge.Services.Files;
using taskforge.Services.Agent;

namespace taskforge.Controllers.Agent
{
    [ApiController]
    [Route("api/agent")]
    [Authorize(Roles = AppRoles.Admin + "," + AppRoles.Editor)]
    public sealed class AgentController : ControllerBase
    {
        private readonly ApplicationDbContext _db;
        private readonly ICurrentUserService _current;
        private readonly ICourseAccessService _courseAccess;
        private readonly IHubContext<AgentHub> _hub;
        private readonly IFileStorageService _store;
        private readonly AgentCourseEditApplyService _courseEditApplier;

        public AgentController(
            ApplicationDbContext db,
            ICurrentUserService current,
            ICourseAccessService courseAccess,
            IHubContext<AgentHub> hub,
            IFileStorageService store,
            AgentCourseEditApplyService courseEditApplier)
        {
            _db = db;
            _current = current;
            _courseAccess = courseAccess;
            _hub = hub;
            _store = store;
            _courseEditApplier = courseEditApplier;
        }

        [HttpGet("conversations")]
        public async Task<IActionResult> List([FromQuery] Guid? courseId, [FromQuery] Guid? assignmentId, [FromQuery] int take = 30)
        {
            var uid = _current.GetUserId();
            take = Math.Clamp(take, 1, 100);

            var query = _db.AgentConversations
                .AsNoTracking()
                .Where(x => x.UserId == uid && !x.IsArchived);

            if (courseId.HasValue) query = query.Where(x => x.CourseId == courseId.Value);
            if (assignmentId.HasValue) query = query.Where(x => x.AssignmentId == assignmentId.Value);

            var conversations = await query
                .OrderByDescending(x => x.UpdatedAtUtc)
                .Take(take)
                .Include(x => x.Messages.OrderByDescending(m => m.CreatedAtUtc).Take(1))
                .Include(x => x.Runs.OrderByDescending(r => r.CreatedAtUtc).Take(1))
                .ToListAsync();

            return Ok(conversations.Select(ToConversationDto).ToList());
        }

        [HttpPost("conversations")]
        public async Task<IActionResult> CreateConversation([FromBody] AgentCreateConversationRequest request)
        {
            var uid = _current.GetUserId();
            var role = _current.GetRole();
            var normalizedCourseId = await EnsureContextAllowedAsync(uid, role, request.CourseId, request.AssignmentId, request.SupportTicketId);

            var now = DateTime.UtcNow;
            var title = NormalizeTitle(request.Title, request.FirstMessage);
            var conversation = new AgentConversation
            {
                Id = Guid.NewGuid(),
                UserId = uid,
                CourseId = normalizedCourseId,
                AssignmentId = request.AssignmentId,
                SupportTicketId = request.SupportTicketId,
                Title = title,
                Mode = string.IsNullOrWhiteSpace(request.Mode) ? "course-assistant" : request.Mode.Trim(),
                MemoryJson = "{}",
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            };

            _db.AgentConversations.Add(conversation);
            await _db.SaveChangesAsync();

            AgentRun? run = null;
            AgentMessage? firstMessage = null;
            if (!string.IsNullOrWhiteSpace(request.FirstMessage))
            {
                (firstMessage, run) = await AddUserMessageAndRunAsync(conversation, request.FirstMessage!, null);
            }

            var dto = ToConversationDto(conversation);
            await BroadcastAsync(conversation.Id, "conversation.created", new { conversation = dto });

            return Ok(new
            {
                conversation = dto,
                message = firstMessage != null ? ToMessageDto(firstMessage) : null,
                run = run != null ? ToRunDto(run) : null,
            });
        }

        [HttpGet("conversations/{conversationId:guid}")]
        public async Task<IActionResult> GetConversation([FromRoute] Guid conversationId)
        {
            var uid = _current.GetUserId();
            var conversation = await _db.AgentConversations
                .AsNoTracking()
                .Include(x => x.Messages.OrderBy(m => m.CreatedAtUtc))
                .Include(x => x.Runs.OrderByDescending(r => r.CreatedAtUtc))
                    .ThenInclude(r => r.Steps.OrderBy(s => s.Seq))
                .Include(x => x.Runs.OrderByDescending(r => r.CreatedAtUtc))
                    .ThenInclude(r => r.Artifacts.OrderBy(a => a.CreatedAtUtc))
                .FirstOrDefaultAsync(x => x.Id == conversationId && x.UserId == uid && !x.IsArchived);

            if (conversation == null) return NotFound();

            var details = new AgentConversationDetailsDto
            {
                Conversation = ToConversationDto(conversation),
                Messages = conversation.Messages.OrderBy(x => x.CreatedAtUtc).Select(ToMessageDto).ToList(),
                Runs = conversation.Runs.OrderByDescending(x => x.CreatedAtUtc).Select(ToRunDto).ToList(),
            };

            return Ok(details);
        }


        [HttpGet("conversations/{conversationId:guid}/debug-dump")]
        public async Task<IActionResult> GetConversationDebugDump([FromRoute] Guid conversationId, [FromQuery] string? format = "text", CancellationToken ct = default)
        {
            var uid = _current.GetUserId();
            var conversationExists = await _db.AgentConversations
                .AsNoTracking()
                .AnyAsync(x => x.Id == conversationId && x.UserId == uid && !x.IsArchived, ct);

            if (!conversationExists) return NotFound(new { message = "AI-чат не найден." });

            object dump;
            try
            {
                dump = await BuildAgentDebugDumpAsync(conversationId, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // Debug dump must be best-effort: the Copy AI dump button should not fail just
                // because one diagnostic projection hits bad/null data.
                dump = new
                {
                    DebugSchemaVersion = 4,
                    GeneratedAtUtc = DateTime.UtcNow,
                    GeneratedByUserId = uid,
                    ConversationId = conversationId,
                    DumpBuildError = new
                    {
                        type = ex.GetType().Name,
                        message = ex.Message,
                        stackTrace = ex.ToString()
                    },
                    RealtimeNote = "Backend debug dump failed while collecting full data; client-side snapshot may still be appended by the React button."
                };
            }

            var asText = string.Equals(format, "text", StringComparison.OrdinalIgnoreCase)
                         || string.Equals(format, "txt", StringComparison.OrdinalIgnoreCase)
                         || string.Equals(format, "log", StringComparison.OrdinalIgnoreCase);

            if (asText)
                return new ContentResult { Content = BuildAgentDebugDumpText(dump), ContentType = "text/plain; charset=utf-8", StatusCode = 200 };

            return Ok(dump);
        }

        [HttpPost("conversations/{conversationId:guid}/attachments")]
        [RequestSizeLimit(25 * 1024 * 1024)]
        public async Task<IActionResult> UploadAttachment([FromRoute] Guid conversationId, [FromForm] IFormFile file, CancellationToken ct)
        {
            var uid = _current.GetUserId();
            var conversation = await _db.AgentConversations.FirstOrDefaultAsync(x => x.Id == conversationId && x.UserId == uid && !x.IsArchived, ct);
            if (conversation == null) return NotFound();
            if (file == null || file.Length <= 0) return BadRequest(new { message = "Файл не передан" });

            var key = await _store.UploadFileAsync(file, $"agent-conversations/{conversationId:N}", ct);
            var attachment = new AgentAttachmentDto
            {
                Key = key,
                FileName = string.IsNullOrWhiteSpace(file.FileName) ? "file" : Path.GetFileName(file.FileName),
                ContentType = string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType,
                SizeBytes = file.Length,
                Url = $"/api/private-files/{Uri.EscapeDataString(key)}",
            };
            return Ok(attachment);
        }


        [HttpPost("conversations/{conversationId:guid}/messages")]
        public async Task<IActionResult> SendMessage([FromRoute] Guid conversationId, [FromBody] AgentSendMessageRequest request)
        {
            var uid = _current.GetUserId();
            var conversation = await _db.AgentConversations.FirstOrDefaultAsync(x => x.Id == conversationId && x.UserId == uid && !x.IsArchived);
            if (conversation == null) return NotFound();

            var attachments = NormalizeAttachments(request.Attachments, conversation.Id);
            if (string.IsNullOrWhiteSpace(request.Text) && attachments.Count == 0)
                throw new ValidationException("Сообщение для AI-чата не может быть пустым.");

            var (message, run) = await AddUserMessageAndRunAsync(conversation, request.Text, request.ClientMessageId, attachments);

            var messageDto = ToMessageDto(message);
            var runDto = ToRunDto(run);
            await BroadcastAsync(conversation.Id, "message.created", new { message = messageDto });
            await BroadcastAsync(conversation.Id, "run.created", new { run = runDto });

            return Ok(new { message = messageDto, run = runDto });
        }


        [HttpPost("conversations/{conversationId:guid}/polish-task")]
        public async Task<IActionResult> PolishGeneratedTask([FromRoute] Guid conversationId, [FromBody] AgentPolishGeneratedTaskRequest request)
        {
            var uid = _current.GetUserId();
            var role = _current.GetRole();
            var conversation = await _db.AgentConversations.FirstOrDefaultAsync(x => x.Id == conversationId && x.UserId == uid && !x.IsArchived);
            if (conversation == null) return NotFound();

            var courseId = await ResolvePolishCourseIdAsync(conversation, request);
            if (courseId.HasValue && !await _courseAccess.CanEditCourseAsync(uid, role, courseId.Value))
                return Forbid();

            if (request.Task.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
                throw new ValidationException("Не передано AI-задание для вылизывания.");

            if (!courseId.HasValue)
                throw new ValidationException("Не определён курс для сохранения AI-черновика. Сгенерируйте задания в контексте курса или выберите курс/позицию вставки.");

            var now = DateTime.UtcNow;
            var draftConversationContext = await BuildDraftConversationContextAsync(conversation.Id);
            var taskTitle = TryGetString(request.Task, "title") ?? TryGetString(request.Task, "Title") ?? $"Задание {request.TaskIndex ?? 1}";
            var message = new AgentMessage
            {
                Id = Guid.NewGuid(),
                ConversationId = conversation.Id,
                Role = "user",
                Source = "ui-action",
                Text = $"Выбрано AI-задание для вылизывания и создания скрытого черновика: {taskTitle}",
                DataJson = JsonSerializer.Serialize(new
                {
                    action = "polish_generated_task",
                    sourceMessageId = request.SourceMessageId,
                    sourceRunId = request.SourceRunId,
                    sourceArtifactId = request.SourceArtifactId,
                    taskIndex = request.TaskIndex,
                    courseId,
                    beforeAssignmentId = request.BeforeAssignmentId,
                    afterAssignmentId = request.AfterAssignmentId,
                    note = request.Note
                }),
                CreatedAtUtc = now,
            };

            var run = new AgentRun
            {
                Id = Guid.NewGuid(),
                ConversationId = conversation.Id,
                RequestedByUserId = uid,
                ActingOnBehalfOfUserId = uid,
                Status = "queued",
                ScenarioId = "polish_assignment_draft",
                Priority = 20,
                Attempt = 0,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                RequestJson = JsonSerializer.Serialize(new
                {
                    action = "polish_assignment_draft",
                    rawText = message.Text,
                    courseId,
                    assignmentId = conversation.AssignmentId,
                    supportTicketId = conversation.SupportTicketId,
                    sourceMessageId = request.SourceMessageId,
                    sourceRunId = request.SourceRunId,
                    sourceArtifactId = request.SourceArtifactId,
                    taskIndex = request.TaskIndex,
                    selectedTask = JsonSerializer.Deserialize<JsonElement>(request.Task.GetRawText()),
                    beforeAssignmentId = request.BeforeAssignmentId,
                    afterAssignmentId = request.AfterAssignmentId,
                    note = request.Note,
                    draftConversationContext,
                    conversationMemory = ParseJson(conversation.MemoryJson ?? "{}"),
                    createHiddenDraft = true,
                    requiredValidation = new { runnerAttempts = 2, requireReferenceSolution = true, requireTests = true, requireRunnerPass = true }
                }),
            };
            message.RunId = run.Id;
            conversation.CourseId ??= courseId;
            conversation.UpdatedAtUtc = now;

            _db.AgentMessages.Add(message);
            _db.AgentRuns.Add(run);
            _db.AgentSteps.Add(new AgentStep
            {
                Id = Guid.NewGuid(),
                RunId = run.Id,
                Seq = 1,
                Kind = "lifecycle",
                Status = "queued",
                ActionName = "polish_assignment_draft",
                Title = "AI-задание выбрано для вылизывания",
                Summary = "Worker улучшит условие, тесты, эталонное решение, прогонит решение на раннерах и создаст скрытый черновик.",
                CreatedAtUtc = now,
                IsVisibleToUser = true,
            });

            await _db.SaveChangesAsync();
            var messageDto = ToMessageDto(message);
            var runDto = ToRunDto(run);
            await BroadcastAsync(conversation.Id, "message.created", new { message = messageDto });
            await BroadcastAsync(conversation.Id, "run.created", new { run = runDto });
            return Ok(new { message = messageDto, run = runDto });
        }

        [HttpPost("conversations/{conversationId:guid}/polish-tasks")]
        public async Task<IActionResult> PolishGeneratedTasks([FromRoute] Guid conversationId, [FromBody] AgentPolishGeneratedTaskBatchRequest request)
        {
            var uid = _current.GetUserId();
            var role = _current.GetRole();
            var conversation = await _db.AgentConversations.FirstOrDefaultAsync(x => x.Id == conversationId && x.UserId == uid && !x.IsArchived);
            if (conversation == null) return NotFound();

            var items = (request.Tasks ?? new List<AgentPolishGeneratedTaskRequest>())
                .Where(x => x.Task.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
                .Take(20)
                .ToList();

            if (items.Count == 0)
                throw new ValidationException("Выберите хотя бы одно AI-задание для вылизывания.");

            var now = DateTime.UtcNow;
            var draftConversationContext = await BuildDraftConversationContextAsync(conversation.Id);
            var prepared = new List<(AgentPolishGeneratedTaskRequest Item, Guid? CourseId, string Title, int TaskIndex)>();
            var selectedForMessage = new List<object>();

            foreach (var item in items)
            {
                var courseId = await ResolvePolishCourseIdAsync(conversation, item);
                if (courseId.HasValue && !await _courseAccess.CanEditCourseAsync(uid, role, courseId.Value))
                    return Forbid();

                var taskIndex = item.TaskIndex ?? prepared.Count + 1;
                var taskTitle = TryGetString(item.Task, "title") ?? TryGetString(item.Task, "Title") ?? $"Задание {taskIndex}";
                prepared.Add((item, courseId, taskTitle, taskIndex));
                selectedForMessage.Add(new
                {
                    taskIndex,
                    title = taskTitle,
                    courseId,
                    beforeAssignmentId = item.BeforeAssignmentId,
                    afterAssignmentId = item.AfterAssignmentId,
                    sourceRunId = item.SourceRunId,
                    sourceMessageId = item.SourceMessageId,
                    sourceArtifactId = item.SourceArtifactId,
                });
            }

            if (prepared.Any(x => !x.CourseId.HasValue))
                throw new ValidationException("Не определён курс для одного или нескольких AI-черновиков. Сгенерируйте задания в контексте курса или укажите позицию вставки.");

            var message = new AgentMessage
            {
                Id = Guid.NewGuid(),
                ConversationId = conversation.Id,
                Role = "user",
                Source = "ui-action",
                Text = prepared.Count == 1
                    ? $"Выбрано AI-задание для вылизывания и создания скрытого черновика: {prepared[0].Title}"
                    : $"Выбрано {prepared.Count} AI-заданий для параллельного вылизывания и создания скрытых черновиков.",
                DataJson = JsonSerializer.Serialize(new
                {
                    action = "polish_generated_tasks_batch",
                    parallelize = request.Parallelize,
                    note = request.Note,
                    selectedTasks = selectedForMessage,
                }),
                CreatedAtUtc = now,
            };

            _db.AgentMessages.Add(message);

            var runs = new List<AgentRun>();
            var primaryCourseId = prepared.Select(x => x.CourseId).FirstOrDefault(x => x.HasValue);
            if (primaryCourseId.HasValue) conversation.CourseId ??= primaryCourseId;
            conversation.UpdatedAtUtc = now;

            foreach (var preparedItem in prepared)
            {
                var item = preparedItem.Item;
                var run = new AgentRun
                {
                    Id = Guid.NewGuid(),
                    ConversationId = conversation.Id,
                    RequestedByUserId = uid,
                    ActingOnBehalfOfUserId = uid,
                    Status = "queued",
                    ScenarioId = "polish_assignment_draft",
                    Priority = 30,
                    Attempt = 0,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now,
                    RequestJson = JsonSerializer.Serialize(new
                    {
                        action = "polish_assignment_draft",
                        batchAction = true,
                        batchMessageId = message.Id,
                        rawText = $"Выбрано AI-задание для вылизывания и создания скрытого черновика: {preparedItem.Title}",
                        courseId = preparedItem.CourseId,
                        assignmentId = conversation.AssignmentId,
                        supportTicketId = conversation.SupportTicketId,
                        sourceMessageId = item.SourceMessageId,
                        sourceRunId = item.SourceRunId,
                        sourceArtifactId = item.SourceArtifactId,
                        taskIndex = preparedItem.TaskIndex,
                        selectedTask = JsonSerializer.Deserialize<JsonElement>(item.Task.GetRawText()),
                        beforeAssignmentId = item.BeforeAssignmentId,
                        afterAssignmentId = item.AfterAssignmentId,
                        note = item.Note ?? request.Note ?? "Пользователь выбрал это AI-задание галочкой для вылизывания и создания скрытого черновика.",
                        draftConversationContext,
                        conversationMemory = ParseJson(conversation.MemoryJson ?? "{}"),
                        createHiddenDraft = true,
                        requiredValidation = new { runnerAttempts = 2, requireReferenceSolution = true, requireTests = true, requireRunnerPass = true }
                    }),
                };

                runs.Add(run);
                _db.AgentRuns.Add(run);
                _db.AgentSteps.Add(new AgentStep
                {
                    Id = Guid.NewGuid(),
                    RunId = run.Id,
                    Seq = 1,
                    Kind = "lifecycle",
                    Status = "queued",
                    ActionName = "polish_assignment_draft",
                    Title = "AI-задание поставлено в очередь на вылизывание",
                    Summary = "Worker улучшит условие, тесты, эталонное решение, прогонит решение на раннерах и создаст скрытый черновик.",
                    OutputJson = JsonSerializer.Serialize(new { batchMessageId = message.Id, taskIndex = preparedItem.TaskIndex, title = preparedItem.Title }),
                    CreatedAtUtc = now,
                    IsVisibleToUser = true,
                });
            }

            await _db.SaveChangesAsync();

            var messageDto = ToMessageDto(message);
            var runDtos = runs.Select(ToRunDto).ToList();
            await BroadcastAsync(conversation.Id, "message.created", new { message = messageDto });
            foreach (var runDto in runDtos)
                await BroadcastAsync(conversation.Id, "run.created", new { run = runDto });

            return Ok(new { message = messageDto, runs = runDtos, count = runDtos.Count, parallelize = request.Parallelize });
        }

        [HttpPost("artifacts/{artifactId:guid}/apply")]
        public async Task<IActionResult> ApplyArtifact([FromRoute] Guid artifactId, [FromBody] AgentApplyArtifactRequest? request, CancellationToken ct)
        {
            return await ApplyArtifactCore(null, artifactId, request, ct);
        }

        [HttpPost("runs/{runId:guid}/artifacts/{artifactId:guid}/apply")]
        public async Task<IActionResult> ApplyRunArtifact([FromRoute] Guid runId, [FromRoute] Guid artifactId, [FromBody] AgentApplyArtifactRequest? request, CancellationToken ct)
        {
            return await ApplyArtifactCore(runId, artifactId, request, ct);
        }

        private async Task<IActionResult> ApplyArtifactCore(Guid? runId, Guid artifactId, AgentApplyArtifactRequest? request, CancellationToken ct)
        {
            var uid = _current.GetUserId();
            var role = _current.GetRole();
            var safeRequest = request ?? new AgentApplyArtifactRequest();
            var result = await _courseEditApplier.ApplyAsync(artifactId, runId, uid, role, safeRequest, ct);
            try
            {
                await AppendArtifactApplyHttpDebugStepAsync(result, safeRequest, ct);
            }
            catch
            {
                // Applying an artifact must not fail just because diagnostic logging failed.
            }

            if (result.ConversationId != Guid.Empty)
                await BroadcastAsync(result.ConversationId, result.Ok ? "artifact.applied" : "artifact.apply_failed", new { result });

            if (!result.Ok)
            {
                if (result.RunId == Guid.Empty) return NotFound(result);
                return BadRequest(result);
            }

            return Ok(result);
        }

        [HttpPost("runs/{runId:guid}/cancel")]
        public async Task<IActionResult> CancelRun([FromRoute] Guid runId, [FromBody] AgentCancelRunRequest? request)
        {
            var uid = _current.GetUserId();
            var run = await _db.AgentRuns
                .Include(x => x.Conversation)
                .FirstOrDefaultAsync(x => x.Id == runId && x.Conversation.UserId == uid);

            if (run == null) return NotFound();
            if (run.Status is "completed" or "failed" or "canceled") return Ok(ToRunDto(run));

            var now = DateTime.UtcNow;
            run.Status = "canceled";
            run.CanceledAtUtc = now;
            run.FinishedAtUtc = now;
            run.UpdatedAtUtc = now;
            run.ErrorJson = JsonSerializer.Serialize(new { reason = request?.Reason ?? "user_requested" });

            _db.AgentSteps.Add(new AgentStep
            {
                Id = Guid.NewGuid(),
                RunId = run.Id,
                Seq = await NextStepSeqAsync(run.Id),
                Kind = "lifecycle",
                Status = "canceled",
                Title = "Остановлено пользователем",
                Summary = "AI-run остановлен до завершения.",
                OutputJson = run.ErrorJson,
                CreatedAtUtc = now,
                FinishedAtUtc = now,
                IsVisibleToUser = true,
            });

            await _db.SaveChangesAsync();
            await BroadcastAsync(run.ConversationId, "run.updated", new { run = ToRunDto(run) });
            return Ok(ToRunDto(run));
        }

        private async Task<(AgentMessage Message, AgentRun Run)> AddUserMessageAndRunAsync(AgentConversation conversation, string text, string? clientMessageId, List<AgentAttachmentDto>? attachments = null)
        {
            var now = DateTime.UtcNow;
            var clean = (text ?? string.Empty).Trim();
            var cleanClientMessageId = string.IsNullOrWhiteSpace(clientMessageId) ? null : clientMessageId.Trim();
            var safeAttachments = NormalizeAttachments(attachments, conversation.Id);

            if (!string.IsNullOrWhiteSpace(cleanClientMessageId))
            {
                var existingMessage = await _db.AgentMessages.AsNoTracking()
                    .Where(x => x.ConversationId == conversation.Id && x.Role == "user" && x.ClientMessageId == cleanClientMessageId)
                    .OrderByDescending(x => x.CreatedAtUtc)
                    .FirstOrDefaultAsync();
                if (existingMessage?.RunId != null)
                {
                    var existingRun = await _db.AgentRuns.AsNoTracking().FirstOrDefaultAsync(x => x.Id == existingMessage.RunId.Value);
                    if (existingRun != null) return (existingMessage, existingRun);
                }
            }

            if (!string.IsNullOrWhiteSpace(clean) && safeAttachments.Count == 0)
            {
                var duplicateCutoff = now.AddSeconds(-2);
                var recentDuplicate = await _db.AgentMessages.AsNoTracking()
                    .Where(x => x.ConversationId == conversation.Id && x.Role == "user" && x.Text == clean && x.CreatedAtUtc >= duplicateCutoff)
                    .OrderByDescending(x => x.CreatedAtUtc)
                    .FirstOrDefaultAsync();
                if (recentDuplicate?.RunId != null)
                {
                    var duplicateRun = await _db.AgentRuns.AsNoTracking().FirstOrDefaultAsync(x => x.Id == recentDuplicate.RunId.Value);
                    if (duplicateRun != null && duplicateRun.Status is not ("completed" or "completed_with_warnings" or "failed" or "canceled"))
                        return (recentDuplicate, duplicateRun);
                }
            }

            var message = new AgentMessage
            {
                Id = Guid.NewGuid(),
                ConversationId = conversation.Id,
                Role = "user",
                Source = "chat",
                Text = string.IsNullOrWhiteSpace(clean) && safeAttachments.Count > 0 ? "[файлы]" : clean,
                DataJson = safeAttachments.Count > 0 ? JsonSerializer.Serialize(new { attachments = safeAttachments }) : null,
                ClientMessageId = cleanClientMessageId,
                CreatedAtUtc = now,
            };

            var run = new AgentRun
            {
                Id = Guid.NewGuid(),
                ConversationId = conversation.Id,
                RequestedByUserId = conversation.UserId,
                ActingOnBehalfOfUserId = conversation.UserId,
                Status = "queued",
                ScenarioId = "assistant_chat_turn",
                Priority = 0,
                Attempt = 0,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                RequestJson = JsonSerializer.Serialize(new
                {
                    rawText = clean,
                    courseId = conversation.CourseId,
                    assignmentId = conversation.AssignmentId,
                    supportTicketId = conversation.SupportTicketId,
                    clientMessageId = message.ClientMessageId,
                    attachments = safeAttachments,
                }),
            };

            message.RunId = run.Id;
            conversation.UpdatedAtUtc = now;
            if (conversation.Title == "AI-чат") conversation.Title = NormalizeTitle(null, string.IsNullOrWhiteSpace(clean) ? "Файлы для AI" : clean);

            _db.AgentMessages.Add(message);
            _db.AgentRuns.Add(run);
            _db.AgentSteps.Add(new AgentStep
            {
                Id = Guid.NewGuid(),
                RunId = run.Id,
                Seq = 1,
                Kind = "lifecycle",
                Status = "queued",
                Title = "Запрос поставлен в очередь",
                Summary = "Worker заберёт задачу, подтянет курс и выберет сценарий.",
                CreatedAtUtc = now,
                IsVisibleToUser = true,
            });

            await _db.SaveChangesAsync();
            return (message, run);
        }

        private async Task<object> BuildDraftConversationContextAsync(Guid conversationId)
        {
            var rows = await _db.AgentMessages.AsNoTracking()
                .Where(x => x.ConversationId == conversationId)
                .OrderByDescending(x => x.CreatedAtUtc)
                .Take(40)
                .OrderBy(x => x.CreatedAtUtc)
                .Select(x => new
                {
                    x.Id,
                    x.Role,
                    x.Source,
                    x.Text,
                    x.CreatedAtUtc,
                    x.DataJson
                })
                .ToListAsync();

            var styleHints = rows
                .Where(x => string.Equals(x.Role, "user", StringComparison.OrdinalIgnoreCase))
                .Select(x => x.Text ?? string.Empty)
                .Where(ContainsDraftStyleHint)
                .TakeLast(12)
                .ToList();

            return new
            {
                purpose = "Preserve chat context for polishing the selected generated task into a hidden draft.",
                instruction = "When building the hidden draft, follow relevant style, format, difficulty, language and course-placement requirements from previous chat messages. The latest explicit task selection is not the only source of instructions.",
                recentMessages = rows.Select(x => new
                {
                    id = x.Id,
                    role = x.Role,
                    source = x.Source,
                    text = x.Text,
                    createdAtUtc = x.CreatedAtUtc,
                    data = ParseJson(x.DataJson)
                }).ToList(),
                styleHints
            };
        }

        private static bool ContainsDraftStyleHint(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            var lower = text.ToLowerInvariant();
            return lower.Contains("стиль")
                   || lower.Contains("стилист")
                   || lower.Contains("оформ")
                   || lower.Contains("формат")
                   || lower.Contains("сложност")
                   || lower.Contains("тон")
                   || lower.Contains("язык")
                   || lower.Contains("требован")
                   || lower.Contains("сделай")
                   || lower.Contains("как раньше")
                   || lower.Contains("так же")
                   || lower.Contains("аналогично");
        }

        private async Task<Guid?> ResolvePolishCourseIdAsync(AgentConversation conversation, AgentPolishGeneratedTaskRequest request)
        {
            var courseId = request.CourseId
                           ?? TryGetGuid(request.Task, "selectedCourseId", "courseId")
                           ?? TryGetNestedGuid(request.Task, "placement", "courseId")
                           ?? conversation.CourseId
                           ?? ExtractActiveCourseIdFromMemory(conversation.MemoryJson);

            if (!courseId.HasValue && request.BeforeAssignmentId.HasValue)
            {
                courseId = await _db.TaskAssignments.AsNoTracking()
                    .Where(x => x.Id == request.BeforeAssignmentId.Value)
                    .Select(x => (Guid?)x.CourseId)
                    .FirstOrDefaultAsync();
            }
            if (!courseId.HasValue && request.AfterAssignmentId.HasValue)
            {
                courseId = await _db.TaskAssignments.AsNoTracking()
                    .Where(x => x.Id == request.AfterAssignmentId.Value)
                    .Select(x => (Guid?)x.CourseId)
                    .FirstOrDefaultAsync();
            }
            return courseId;
        }

        private static Guid? TryGetGuid(JsonElement element, params string[] names)
        {
            var value = TryGetString(element, names);
            return Guid.TryParse(value, out var id) ? id : null;
        }

        private static Guid? TryGetNestedGuid(JsonElement element, string objectName, params string[] names)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(objectName, out var obj) || obj.ValueKind != JsonValueKind.Object)
                return null;
            return TryGetGuid(obj, names);
        }

        private static Guid? ExtractActiveCourseIdFromMemory(string? memoryJson)
        {
            if (string.IsNullOrWhiteSpace(memoryJson)) return null;
            try
            {
                using var doc = JsonDocument.Parse(memoryJson);
                if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
                foreach (var key in new[] { "activeCourseId", "selectedCourseId", "courseId" })
                {
                    if (doc.RootElement.TryGetProperty(key, out var prop)
                        && prop.ValueKind == JsonValueKind.String
                        && Guid.TryParse(prop.GetString(), out var id))
                        return id;
                }
                if (doc.RootElement.TryGetProperty("currentDraftBlueprint", out var draft)
                    && draft.ValueKind == JsonValueKind.Object
                    && TryGetGuid(draft, "selectedCourseId", "courseId") is Guid draftCourseId)
                    return draftCourseId;
            }
            catch
            {
            }
            return null;
        }

        private async Task<Guid?> EnsureContextAllowedAsync(Guid userId, string? role, Guid? courseId, Guid? assignmentId, Guid? supportTicketId)
        {
            if (assignmentId.HasValue)
            {
                var assignmentCourseId = await _db.TaskAssignments.AsNoTracking()
                    .Where(x => x.Id == assignmentId.Value)
                    .Select(x => (Guid?)x.CourseId)
                    .FirstOrDefaultAsync();

                if (assignmentCourseId == null) throw new KeyNotFoundException("Задание не найдено.");
                courseId ??= assignmentCourseId.Value;
            }

            if (courseId.HasValue && !await _courseAccess.CanViewCourseAsync(userId, role, courseId.Value))
                throw new UnauthorizedAccessException("Нет доступа к курсу для AI-чата.");

            if (supportTicketId.HasValue)
            {
                var canSeeTicket = await _db.SupportTickets.AsNoTracking()
                    .AnyAsync(x => x.Id == supportTicketId.Value && (x.UserId == userId || _current.IsAdminOrEditor()));
                if (!canSeeTicket) throw new UnauthorizedAccessException("Нет доступа к тикету для AI-чата.");
            }

            return courseId;
        }


        private async Task AppendArtifactApplyHttpDebugStepAsync(AgentApplyArtifactResult result, AgentApplyArtifactRequest request, CancellationToken ct)
        {
            if (result.RunId == Guid.Empty) return;

            var now = DateTime.UtcNow;
            var step = new AgentStep
            {
                Id = Guid.NewGuid(),
                RunId = result.RunId,
                Seq = await NextStepSeqAsync(result.RunId),
                Kind = "debug",
                Status = result.Ok ? "completed" : "failed",
                ActionName = "artifact_apply_http_request",
                Title = result.Ok ? "Apply-запрос к AI artifact обработан" : "Apply-запрос к AI artifact отклонён",
                Summary = result.Message,
                InputJson = SafeSerializeForDebug(new
                {
                    artifactId = result.ArtifactId,
                    artifactType = result.ArtifactType,
                    dryRun = result.DryRun,
                    force = request.Force,
                    note = TrimForDebug(request.Note, 2000)
                }),
                OutputJson = SafeSerializeForDebug(result),
                CreatedAtUtc = now,
                FinishedAtUtc = now,
                IsVisibleToUser = false,
            };

            _db.AgentSteps.Add(step);
            await _db.SaveChangesAsync(ct);

            if (result.ConversationId != Guid.Empty)
                await BroadcastAsync(result.ConversationId, "step.created", new { step = ToStepDto(step) });
        }

        private async Task<object> BuildAgentDebugDumpAsync(Guid conversationId, CancellationToken ct)
        {
            var conversation = await _db.AgentConversations
                .AsNoTracking()
                .FirstAsync(x => x.Id == conversationId, ct);

            var messages = await _db.AgentMessages
                .AsNoTracking()
                .Where(x => x.ConversationId == conversationId)
                .OrderBy(x => x.CreatedAtUtc)
                .Select(x => new
                {
                    x.Id,
                    x.ConversationId,
                    x.RunId,
                    x.Role,
                    x.Source,
                    x.Text,
                    x.ClientMessageId,
                    // CreatedAtUtc is intentionally omitted here. Some historic rows may contain NULL even
                    // when the EF model has a non-null timestamp; projecting it through EF can throw
                    // "Nullable object must have a value" and break the whole debug dump.
                    RawDataJson = x.DataJson,
                    Data = ParseJson(x.DataJson),
                    Attachments = ExtractAttachments(x.DataJson)
                })
                .ToListAsync(ct);

            var runs = await _db.AgentRuns
                .AsNoTracking()
                .Where(x => x.ConversationId == conversationId)
                .OrderByDescending(x => x.CreatedAtUtc)
                .Select(x => new
                {
                    x.Id,
                    x.ConversationId,
                    x.RequestedByUserId,
                    x.ActingOnBehalfOfUserId,
                    x.Status,
                    x.ScenarioId,
                    x.WorkerId,
                    x.Priority,
                    x.Attempt,
                    CreatedAtUtc = (DateTime?)x.CreatedAtUtc,
                    UpdatedAtUtc = (DateTime?)x.UpdatedAtUtc,
                    x.StartedAtUtc,
                    x.FinishedAtUtc,
                    x.LeaseExpiresAtUtc,
                    x.NextWakeAtUtc,
                    x.CanceledAtUtc,
                    RawRequestJson = x.RequestJson,
                    Request = ParseJson(x.RequestJson),
                    RawResultJson = x.ResultJson,
                    Result = ParseJson(x.ResultJson),
                    RawErrorJson = x.ErrorJson,
                    Error = ParseJson(x.ErrorJson),
                    RawDebugJson = x.DebugJson,
                    Debug = ParseJson(x.DebugJson),
                    DurMs = x.StartedAtUtc.HasValue && x.FinishedAtUtc.HasValue
                        ? (double?)(x.FinishedAtUtc.Value - x.StartedAtUtc.Value).TotalMilliseconds
                        : null
                })
                .ToListAsync(ct);

            var runIds = runs.Select(x => x.Id).ToList();

            var steps = await _db.AgentSteps
                .AsNoTracking()
                .Where(x => runIds.Contains(x.RunId))
                .OrderBy(x => x.RunId)
                .ThenBy(x => x.Seq)
                .Select(x => new
                {
                    x.Id,
                    x.RunId,
                    x.Seq,
                    x.Kind,
                    x.Status,
                    x.ActionName,
                    x.Title,
                    x.Summary,
                    x.IsVisibleToUser,
                    CreatedAtUtc = (DateTime?)x.CreatedAtUtc,
                    x.StartedAtUtc,
                    x.FinishedAtUtc,
                    RawInputJson = x.InputJson,
                    Input = ParseJson(x.InputJson),
                    RawOutputJson = x.OutputJson,
                    Output = ParseJson(x.OutputJson),
                    RawErrorJson = x.ErrorJson,
                    Error = ParseJson(x.ErrorJson),
                    DurMs = x.StartedAtUtc.HasValue && x.FinishedAtUtc.HasValue
                        ? (double?)(x.FinishedAtUtc.Value - x.StartedAtUtc.Value).TotalMilliseconds
                        : null
                })
                .ToListAsync(ct);

            var dumpSectionErrors = new List<object>();
            var artifacts = new List<AgentDebugArtifactRow>();
            try
            {
                artifacts = await _db.AgentRunArtifacts
                    .AsNoTracking()
                    .Where(x => runIds.Contains(x.RunId))
                    .OrderBy(x => x.Id)
                    .Select(x => new AgentDebugArtifactRow
                    {
                        Id = x.Id,
                        RunId = x.RunId,
                        Type = x.Type,
                        Title = x.Title,
                        StorageKey = x.StorageKey,
                        ContentHash = x.ContentHash,
                        // CreatedAtUtc is intentionally omitted here. Some historic rows may contain NULL even
                        // when the EF model has a non-null timestamp; projecting it through EF can throw
                        // "Nullable object must have a value" and break the whole debug dump.
                        RawDataJson = x.DataJson,
                        Data = ParseJson(x.DataJson),
                        DataJsonLength = x.DataJson == null ? 0 : x.DataJson.Length
                    })
                    .ToListAsync(ct);
            }
            catch (Exception ex)
            {
                dumpSectionErrors.Add(new
                {
                    section = "artifacts",
                    type = ex.GetType().Name,
                    message = ex.Message
                });
            }

            var nullableRunIds = runIds.Select(x => (Guid?)x).ToList();
            var hiddenDrafts = await _db.TaskAssignments
                .AsNoTracking()
                .Where(x => nullableRunIds.Contains(x.SourceAgentRunId))
                .OrderBy(x => x.Id)
                .Select(x => new
                {
                    x.Id,
                    x.CourseId,
                    x.Title,
                    x.Type,
                    x.Sort,
                    x.Rating,
                    x.Difficulty,
                    x.IsHidden,
                    x.LifecycleStatus,
                    x.IsAiDraft,
                    x.SourceAgentRunId,
                    x.SourceAgentArtifactId,
                    x.SourceAgentTaskIndex,
                    // CreatedAt/UpdatedAt are omitted from debug dump for old rows that may contain NULLs.
                    x.PolishedAtUtc,
                    x.PublishedAtUtc,
                    TestCount = x.TestCases.Count,
                    AiDraftJsonLength = x.AiDraftJson == null ? 0 : x.AiDraftJson.Length
                })
                .ToListAsync(ct);

            object? course = null;
            if (conversation.CourseId is Guid debugCourseId)
            {
                course = await _db.Courses
                    .AsNoTracking()
                    .Where(x => x.Id == debugCourseId)
                    .Select(x => new
                    {
                        x.Id,
                        x.Title,
                        x.Description,
                        x.OwnerId,
                        x.IsPublic,
                        // CreatedAt/UpdatedAt omitted for dump robustness.
                        AssignmentCount = x.Assignments.Count
                    })
                    .FirstOrDefaultAsync(ct);
            }

            object? assignment = null;
            if (conversation.AssignmentId is Guid debugAssignmentId)
            {
                assignment = await _db.TaskAssignments
                    .AsNoTracking()
                    .Where(x => x.Id == debugAssignmentId)
                    .Select(x => new
                    {
                        x.Id,
                        x.CourseId,
                        x.Title,
                        x.Type,
                        x.Sort,
                        x.Rating,
                        x.Difficulty,
                        x.IsHidden,
                        x.LifecycleStatus,
                        x.IsAiDraft,
                        x.SourceAgentRunId,
                        x.SourceAgentArtifactId,
                        TestCount = x.TestCases.Count
                    })
                    .FirstOrDefaultAsync(ct);
            }

            var runSummaries = runs.Select(r => new
            {
                r.Id,
                r.Status,
                r.ScenarioId,
                r.WorkerId,
                r.Attempt,
                r.CreatedAtUtc,
                r.StartedAtUtc,
                r.FinishedAtUtc,
                r.DurMs,
                StepCount = steps.Count(s => s.RunId == r.Id),
                ArtifactCount = artifacts.Count(a => a.RunId == r.Id),
                VisibleStepCount = steps.Count(s => s.RunId == r.Id && s.IsVisibleToUser),
                HiddenDebugStepCount = steps.Count(s => s.RunId == r.Id && !s.IsVisibleToUser),
                LastStep = steps.Where(s => s.RunId == r.Id).OrderByDescending(s => s.Seq).Select(s => new { s.Seq, s.Kind, s.Status, s.ActionName, s.Title, s.Summary }).FirstOrDefault(),
                ArtifactTypes = artifacts.Where(a => a.RunId == r.Id).GroupBy(a => a.Type).Select(g => new { Type = g.Key, Count = g.Count() }).ToList()
            }).ToList();

            return new
            {
                DebugSchemaVersion = 4,
                GeneratedAtUtc = DateTime.UtcNow,
                GeneratedByUserId = _current.GetUserId(),
                GeneratedByRole = _current.GetRole(),
                Conversation = new
                {
                    conversation.Id,
                    conversation.UserId,
                    conversation.CourseId,
                    conversation.AssignmentId,
                    conversation.SupportTicketId,
                    conversation.Title,
                    conversation.Mode,
                    conversation.IsArchived,
                    conversation.CreatedAtUtc,
                    conversation.UpdatedAtUtc,
                    RawMemoryJson = conversation.MemoryJson,
                    Memory = ParseJson(conversation.MemoryJson)
                },
                Context = new { Course = course, Assignment = assignment },
                Stats = new
                {
                    MessageCount = messages.Count,
                    RunCount = runs.Count,
                    StepCount = steps.Count,
                    VisibleStepCount = steps.Count(x => x.IsVisibleToUser),
                    HiddenDebugStepCount = steps.Count(x => !x.IsVisibleToUser),
                    ArtifactCount = artifacts.Count,
                    HiddenDraftCount = hiddenDrafts.Count,
                    FailedRunCount = runs.Count(x => string.Equals(x.Status, "failed", StringComparison.OrdinalIgnoreCase)),
                    WaitingApprovalRunCount = runs.Count(x => string.Equals(x.Status, "waiting_approval", StringComparison.OrdinalIgnoreCase))
                },
                RunSummaries = runSummaries,
                Messages = messages,
                Runs = runs,
                Steps = steps,
                Artifacts = artifacts,
                HiddenDrafts = hiddenDrafts,
                DumpSectionErrors = dumpSectionErrors,
                RealtimeNote = "Client-side SignalR events are appended by the React button after this backend dump."
            };
        }

        private sealed class AgentDebugArtifactRow
        {
            public Guid Id { get; set; }
            public Guid RunId { get; set; }
            public string? Type { get; set; }
            public string? Title { get; set; }
            public string? StorageKey { get; set; }
            public string? ContentHash { get; set; }
            public string? RawDataJson { get; set; }
            public object? Data { get; set; }
            public int DataJsonLength { get; set; }
        }

        private static string BuildAgentDebugDumpText(object dump)
        {
            var json = JsonSerializer.Serialize(dump, new JsonSerializerOptions { WriteIndented = true });
            var sb = new StringBuilder();
            sb.AppendLine("TASKFORGE AI DEBUG DUMP");
            sb.AppendLine("Generated by /api/agent/conversations/{conversationId}/debug-dump");
            sb.AppendLine("Contains raw AI request/result JSON, messages, runs, steps, artifacts, proposals, apply attempts and hidden drafts.");
            sb.AppendLine(new string('=', 96));
            sb.AppendLine(json);
            return sb.ToString();
        }

        private static string? TrimForDebug(string? value, int max)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            value = value.Trim();
            return value.Length <= max ? value : value[..max] + "…";
        }

        private async Task<int> NextStepSeqAsync(Guid runId)
        {
            return (await _db.AgentSteps.Where(x => x.RunId == runId).Select(x => (int?)x.Seq).MaxAsync() ?? 0) + 1;
        }

        private async Task BroadcastAsync(Guid conversationId, string type, object payload)
        {
            await _hub.Clients.Group(AgentHub.ConversationGroup(conversationId)).SendAsync(
                AgentHub.EventMethod,
                new { type, conversationId, payload, at = DateTime.UtcNow },
                HttpContext.RequestAborted);
        }


        private static string? TryGetString(JsonElement element, params string[] names)
        {
            if (element.ValueKind != JsonValueKind.Object) return null;
            foreach (var name in names)
            {
                if (element.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String)
                    return prop.GetString();
            }
            return null;
        }


        private static List<AgentAttachmentDto> NormalizeAttachments(IEnumerable<AgentAttachmentDto>? attachments, Guid conversationId)
        {
            var result = new List<AgentAttachmentDto>();
            var expectedPrefix = $"agent-conversations/{conversationId:N}/";
            foreach (var item in attachments ?? Enumerable.Empty<AgentAttachmentDto>())
            {
                if (string.IsNullOrWhiteSpace(item.Key)) continue;
                var key = item.Key.Trim();
                if (!key.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                result.Add(new AgentAttachmentDto
                {
                    Key = key,
                    FileName = string.IsNullOrWhiteSpace(item.FileName) ? Path.GetFileName(key) : Path.GetFileName(item.FileName),
                    ContentType = string.IsNullOrWhiteSpace(item.ContentType) ? "application/octet-stream" : item.ContentType.Trim(),
                    SizeBytes = Math.Max(0, item.SizeBytes),
                    Url = $"/api/private-files/{Uri.EscapeDataString(key)}",
                    ExtractedText = null,
                });
            }
            return result.Take(10).ToList();
        }

        private static List<AgentAttachmentDto> ExtractAttachments(string? dataJson)
        {
            if (string.IsNullOrWhiteSpace(dataJson)) return new List<AgentAttachmentDto>();
            try
            {
                using var doc = JsonDocument.Parse(dataJson);
                if (!doc.RootElement.TryGetProperty("attachments", out var arr) || arr.ValueKind != JsonValueKind.Array)
                    return new List<AgentAttachmentDto>();
                var result = new List<AgentAttachmentDto>();
                foreach (var item in arr.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    var key = TryGetString(item, "key", "Key");
                    if (string.IsNullOrWhiteSpace(key)) continue;
                    result.Add(new AgentAttachmentDto
                    {
                        Key = key,
                        FileName = TryGetString(item, "fileName", "FileName", "name") ?? Path.GetFileName(key),
                        ContentType = TryGetString(item, "contentType", "ContentType") ?? "application/octet-stream",
                        SizeBytes = TryGetLong(item, "sizeBytes", "SizeBytes", "size") ?? 0,
                        Url = TryGetString(item, "url", "Url") ?? $"/api/private-files/{Uri.EscapeDataString(key)}",
                        ExtractedText = TryGetString(item, "extractedText", "ExtractedText"),
                    });
                }
                return result;
            }
            catch
            {
                return new List<AgentAttachmentDto>();
            }
        }

        private static long? TryGetLong(JsonElement element, params string[] names)
        {
            if (element.ValueKind != JsonValueKind.Object) return null;
            foreach (var name in names)
            {
                if (!element.TryGetProperty(name, out var prop)) continue;
                if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt64(out var n)) return n;
                if (prop.ValueKind == JsonValueKind.String && long.TryParse(prop.GetString(), out var parsed)) return parsed;
            }
            return null;
        }

        private static string NormalizeTitle(string? title, string? firstMessage)
        {
            var raw = !string.IsNullOrWhiteSpace(title) ? title! : firstMessage ?? "AI-чат";
            raw = raw.Trim().Replace('\n', ' ').Replace('\r', ' ');
            if (raw.Length > 80) raw = raw[..80].TrimEnd() + "…";
            return string.IsNullOrWhiteSpace(raw) ? "AI-чат" : raw;
        }

        private static AgentConversationDto ToConversationDto(AgentConversation conversation)
        {
            var lastMessage = conversation.Messages?.OrderByDescending(x => x.CreatedAtUtc).FirstOrDefault();
            var lastRun = conversation.Runs?.OrderByDescending(x => x.CreatedAtUtc).FirstOrDefault();
            return new AgentConversationDto
            {
                Id = conversation.Id,
                UserId = conversation.UserId,
                CourseId = conversation.CourseId,
                AssignmentId = conversation.AssignmentId,
                SupportTicketId = conversation.SupportTicketId,
                Title = conversation.Title,
                Mode = conversation.Mode,
                CreatedAtUtc = conversation.CreatedAtUtc,
                UpdatedAtUtc = conversation.UpdatedAtUtc,
                LastMessageText = lastMessage?.Text,
                LastRunStatus = lastRun?.Status,
            };
        }

        private static AgentMessageDto ToMessageDto(AgentMessage message) => new()
        {
            Id = message.Id,
            ConversationId = message.ConversationId,
            RunId = message.RunId,
            Role = message.Role,
            Text = message.Text,
            Source = message.Source,
            Data = ParseJsonElement(message.DataJson),
            Attachments = ExtractAttachments(message.DataJson),
            ClientMessageId = message.ClientMessageId,
            CreatedAtUtc = message.CreatedAtUtc,
        };

        private static AgentRunDto ToRunDto(AgentRun run) => new()
        {
            Id = run.Id,
            ConversationId = run.ConversationId,
            Status = run.Status,
            ScenarioId = run.ScenarioId,
            WorkerId = run.WorkerId,
            Attempt = run.Attempt,
            CreatedAtUtc = run.CreatedAtUtc,
            UpdatedAtUtc = run.UpdatedAtUtc,
            StartedAtUtc = run.StartedAtUtc,
            FinishedAtUtc = run.FinishedAtUtc,
            Result = ParseJsonElement(run.ResultJson),
            Error = ParseJsonElement(run.ErrorJson),
            Steps = run.Steps?.OrderBy(x => x.Seq).Select(ToStepDto).ToList() ?? new List<AgentStepDto>(),
            Artifacts = run.Artifacts?.OrderBy(x => x.CreatedAtUtc).Select(ToArtifactDto).ToList() ?? new List<AgentRunArtifactDto>(),
        };

        private static AgentStepDto ToStepDto(AgentStep step) => new()
        {
            Id = step.Id,
            RunId = step.RunId,
            Seq = step.Seq,
            Kind = step.Kind,
            Status = step.Status,
            ActionName = step.ActionName,
            Title = step.Title,
            Summary = step.Summary,
            Input = ParseJsonElement(step.InputJson),
            Output = ParseJsonElement(step.OutputJson),
            Error = ParseJsonElement(step.ErrorJson),
            IsVisibleToUser = step.IsVisibleToUser,
            CreatedAtUtc = step.CreatedAtUtc,
            StartedAtUtc = step.StartedAtUtc,
            FinishedAtUtc = step.FinishedAtUtc,
        };

        private static AgentRunArtifactDto ToArtifactDto(AgentRunArtifact artifact) => new()
        {
            Id = artifact.Id,
            RunId = artifact.RunId,
            Type = artifact.Type,
            Title = artifact.Title,
            Data = ParseJsonElement(artifact.DataJson),
            StorageKey = artifact.StorageKey,
            ContentHash = artifact.ContentHash,
            CreatedAtUtc = artifact.CreatedAtUtc,
        };

        private static string SafeSerializeForDebug(object? value)
        {
            try
            {
                return JsonSerializer.Serialize(value);
            }
            catch (Exception ex)
            {
                return JsonSerializer.Serialize(new
                {
                    serializationError = ex.GetType().Name,
                    message = ex.Message
                });
            }
        }

        private static object? ParseJson(string? json) => ParseJsonElement(json);

        private static JsonElement? ParseJsonElement(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                using var doc = JsonDocument.Parse(json);
                return doc.RootElement.Clone();
            }
            catch
            {
                return null;
            }
        }
    }
}
