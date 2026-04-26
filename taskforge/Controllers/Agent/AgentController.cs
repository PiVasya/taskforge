using System.ComponentModel.DataAnnotations;
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

namespace taskforge.Controllers.Agent
{
    [ApiController]
    [Route("api/agent")]
    [Authorize(Roles = AppRoles.Admin)]
    public sealed class AgentController : ControllerBase
    {
        private readonly ApplicationDbContext _db;
        private readonly ICurrentUserService _current;
        private readonly ICourseAccessService _courseAccess;
        private readonly IHubContext<AgentHub> _hub;

        public AgentController(
            ApplicationDbContext db,
            ICurrentUserService current,
            ICourseAccessService courseAccess,
            IHubContext<AgentHub> hub)
        {
            _db = db;
            _current = current;
            _courseAccess = courseAccess;
            _hub = hub;
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

        [HttpPost("conversations/{conversationId:guid}/messages")]
        public async Task<IActionResult> SendMessage([FromRoute] Guid conversationId, [FromBody] AgentSendMessageRequest request)
        {
            var uid = _current.GetUserId();
            var conversation = await _db.AgentConversations.FirstOrDefaultAsync(x => x.Id == conversationId && x.UserId == uid && !x.IsArchived);
            if (conversation == null) return NotFound();

            if (string.IsNullOrWhiteSpace(request.Text))
                throw new ValidationException("Сообщение для AI-чата не может быть пустым.");

            var (message, run) = await AddUserMessageAndRunAsync(conversation, request.Text, request.ClientMessageId);

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

            var courseId = request.CourseId ?? conversation.CourseId;
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
            if (courseId.HasValue && !await _courseAccess.CanViewCourseAsync(uid, role, courseId.Value))
                return Forbid();

            if (request.Task.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
                throw new ValidationException("Не передано AI-задание для вылизывания.");

            var now = DateTime.UtcNow;
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
                    createHiddenDraft = true,
                    requiredValidation = new { runnerAttempts = 2, requireReferenceSolution = true, requireTests = true }
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

        private async Task<(AgentMessage Message, AgentRun Run)> AddUserMessageAndRunAsync(AgentConversation conversation, string text, string? clientMessageId)
        {
            var now = DateTime.UtcNow;
            var clean = text.Trim();
            var cleanClientMessageId = string.IsNullOrWhiteSpace(clientMessageId) ? null : clientMessageId.Trim();

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

            var message = new AgentMessage
            {
                Id = Guid.NewGuid(),
                ConversationId = conversation.Id,
                Role = "user",
                Source = "chat",
                Text = clean,
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
                }),
            };

            message.RunId = run.Id;
            conversation.UpdatedAtUtc = now;
            if (conversation.Title == "AI-чат") conversation.Title = NormalizeTitle(null, clean);

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
