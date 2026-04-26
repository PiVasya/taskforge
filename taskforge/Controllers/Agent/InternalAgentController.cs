using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using taskforge.Data;
using taskforge.Data.Models.DTO.Agent;
using taskforge.Data.Models.Entities;
using taskforge.Hubs;

namespace taskforge.Controllers.Agent
{
    [ApiController]
    [Route("api/internal/agent")]
    [AllowAnonymous]
    public sealed class InternalAgentController : ControllerBase
    {
        private readonly ApplicationDbContext _db;
        private readonly IConfiguration _config;
        private readonly IHubContext<AgentHub> _hub;

        public InternalAgentController(ApplicationDbContext db, IConfiguration config, IHubContext<AgentHub> hub)
        {
            _db = db;
            _config = config;
            _hub = hub;
        }

        [HttpPost("claim-next")]
        public async Task<IActionResult> ClaimNext([FromBody] InternalAgentClaimNextRequest request)
        {
            if (!IsAuthorized()) return Unauthorized();

            var now = DateTime.UtcNow;
            var workerId = CleanWorkerId(request.WorkerId);

            var run = await _db.AgentRuns
                .Include(x => x.Conversation)
                .Where(x =>
                    x.Status == "queued" ||
                    (x.Status == "sleeping" && (x.NextWakeAtUtc == null || x.NextWakeAtUtc <= now)) ||
                    (x.Status == "running" && x.LeaseExpiresAtUtc != null && x.LeaseExpiresAtUtc <= now))
                .OrderByDescending(x => x.Priority)
                .ThenBy(x => x.CreatedAtUtc)
                .FirstOrDefaultAsync();

            if (run == null) return Ok(new { job = (object?)null });

            run.Status = "running";
            run.WorkerId = workerId;
            run.Attempt += 1;
            run.StartedAtUtc ??= now;
            run.UpdatedAtUtc = now;
            run.LeaseExpiresAtUtc = now.AddSeconds(90);

            _db.AgentSteps.Add(new AgentStep
            {
                Id = Guid.NewGuid(),
                RunId = run.Id,
                Seq = await NextStepSeqAsync(run.Id),
                Kind = "lifecycle",
                Status = "running",
                ActionName = "claim_next",
                Title = "AI-worker начал работу",
                Summary = "Контекст чата и курса передан во внешний контейнер.",
                OutputJson = JsonSerializer.Serialize(new { workerId, runId = run.Id }),
                CreatedAtUtc = now,
                StartedAtUtc = now,
                IsVisibleToUser = true,
            });

            await _db.SaveChangesAsync();

            var job = await BuildWorkerJobAsync(run.Id);
            await BroadcastAsync(run.ConversationId, "run.updated", new { runId = run.Id, status = run.Status, workerId });
            await BroadcastAsync(run.ConversationId, "step.created", new { runId = run.Id, title = "AI-worker начал работу", status = "running" });

            return Ok(new { job });
        }

        [HttpPost("runs/{runId:guid}/heartbeat")]
        public async Task<IActionResult> Heartbeat([FromRoute] Guid runId, [FromBody] InternalAgentHeartbeatRequest request)
        {
            if (!IsAuthorized()) return Unauthorized();

            var run = await _db.AgentRuns.FirstOrDefaultAsync(x => x.Id == runId);
            if (run == null) return NotFound();

            run.WorkerId = CleanWorkerId(request.WorkerId);
            run.LeaseExpiresAtUtc = DateTime.UtcNow.AddSeconds(90);
            run.UpdatedAtUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            return Ok(new { ok = true, runId = run.Id, leaseExpiresAtUtc = run.LeaseExpiresAtUtc });
        }

        [HttpPost("runs/{runId:guid}/steps")]
        public async Task<IActionResult> AppendStep([FromRoute] Guid runId, [FromBody] InternalAgentAppendStepRequest request)
        {
            if (!IsAuthorized()) return Unauthorized();

            var run = await _db.AgentRuns.FirstOrDefaultAsync(x => x.Id == runId);
            if (run == null) return NotFound();

            var now = DateTime.UtcNow;
            var step = request.Step;
            var kind = GetString(step, "kind") ?? "worker";
            var status = GetString(step, "status") ?? "completed";
            var title = GetString(step, "title", "label", "actionName", "scenarioId") ?? HumanizeStep(kind, status);
            var summary = GetString(step, "summary", "message", "assistant_message", "reason");
            var actionName = GetString(step, "actionName", "action", "scenarioId", "scenario_id");

            var entity = new AgentStep
            {
                Id = Guid.NewGuid(),
                RunId = run.Id,
                Seq = await NextStepSeqAsync(run.Id),
                Kind = kind,
                Status = status,
                ActionName = actionName,
                Title = title,
                Summary = summary,
                OutputJson = step.ValueKind == JsonValueKind.Undefined ? null : step.GetRawText(),
                CreatedAtUtc = now,
                FinishedAtUtc = IsTerminalStepStatus(status) ? now : null,
                IsVisibleToUser = true,
            };

            _db.AgentSteps.Add(entity);
            run.UpdatedAtUtc = now;
            run.WorkerId = CleanWorkerId(request.WorkerId);
            run.LeaseExpiresAtUtc = now.AddSeconds(90);
            await _db.SaveChangesAsync();

            await BroadcastAsync(run.ConversationId, "step.created", new { step = ToStepPayload(entity) });
            return Ok(new { ok = true, stepId = entity.Id });
        }

        [HttpPost("runs/{runId:guid}/complete")]
        public async Task<IActionResult> Complete([FromRoute] Guid runId, [FromBody] InternalAgentCompleteRunRequest request)
        {
            if (!IsAuthorized()) return Unauthorized();

            var run = await _db.AgentRuns
                .Include(x => x.Conversation)
                .FirstOrDefaultAsync(x => x.Id == runId);
            if (run == null) return NotFound();

            var now = DateTime.UtcNow;
            var result = request.Result;
            var rawResult = result.ValueKind == JsonValueKind.Undefined ? "{}" : result.GetRawText();
            var assistantText = GetString(result, "assistantMessage", "assistant_message", "summary")
                                ?? "Готово. Я подготовил результат и приложил его к этому AI-run.";
            var scenarioId = GetString(result, "scenarioId", "scenario_id") ?? run.ScenarioId;
            var status = GetString(result, "status") ?? "completed";

            run.Status = status.StartsWith("completed", StringComparison.OrdinalIgnoreCase) ? status : "completed";
            run.ScenarioId = scenarioId;
            run.ResultJson = rawResult;
            run.WorkerId = CleanWorkerId(request.WorkerId);
            run.UpdatedAtUtc = now;
            run.FinishedAtUtc = now;
            run.LeaseExpiresAtUtc = null;

            var message = new AgentMessage
            {
                Id = Guid.NewGuid(),
                ConversationId = run.ConversationId,
                RunId = run.Id,
                Role = "assistant",
                Source = "worker",
                Text = assistantText,
                DataJson = rawResult,
                CreatedAtUtc = now,
            };
            _db.AgentMessages.Add(message);

            _db.AgentSteps.Add(new AgentStep
            {
                Id = Guid.NewGuid(),
                RunId = run.Id,
                Seq = await NextStepSeqAsync(run.Id),
                Kind = "final",
                Status = "completed",
                ActionName = scenarioId,
                Title = "AI закончил ответ",
                Summary = assistantText,
                OutputJson = rawResult,
                CreatedAtUtc = now,
                FinishedAtUtc = now,
                IsVisibleToUser = true,
            });

            foreach (var artifact in ExtractArtifacts(run.Id, result, now))
                _db.AgentRunArtifacts.Add(artifact);

            var memoryPatchRaw = GetRaw(result, "memoryPatch", "memory_patch");
            if (!string.IsNullOrWhiteSpace(memoryPatchRaw))
                run.Conversation.MemoryJson = memoryPatchRaw;

            run.Conversation.UpdatedAtUtc = now;
            await _db.SaveChangesAsync();

            await BroadcastAsync(run.ConversationId, "message.created", new { message = ToMessagePayload(message) });
            await BroadcastAsync(run.ConversationId, "run.completed", new { runId = run.Id, status = run.Status, result = ParseJson(rawResult) });
            return Ok(new { ok = true, runId = run.Id });
        }

        [HttpPost("runs/{runId:guid}/fail")]
        public async Task<IActionResult> Fail([FromRoute] Guid runId, [FromBody] InternalAgentFailRunRequest request)
        {
            if (!IsAuthorized()) return Unauthorized();

            var run = await _db.AgentRuns.Include(x => x.Conversation).FirstOrDefaultAsync(x => x.Id == runId);
            if (run == null) return NotFound();

            var now = DateTime.UtcNow;
            var errorRaw = request.Error.ValueKind == JsonValueKind.Undefined ? "{}" : request.Error.GetRawText();
            var errorMessage = GetString(request.Error, "message", "error") ?? "AI-worker не смог завершить задачу.";

            run.Status = "failed";
            run.ErrorJson = errorRaw;
            run.WorkerId = CleanWorkerId(request.WorkerId);
            run.UpdatedAtUtc = now;
            run.FinishedAtUtc = now;
            run.LeaseExpiresAtUtc = null;
            run.Conversation.UpdatedAtUtc = now;

            var message = new AgentMessage
            {
                Id = Guid.NewGuid(),
                ConversationId = run.ConversationId,
                RunId = run.Id,
                Role = "assistant",
                Source = "worker",
                Text = $"Я не смог завершить задачу: {errorMessage}",
                DataJson = errorRaw,
                CreatedAtUtc = now,
            };
            _db.AgentMessages.Add(message);

            _db.AgentSteps.Add(new AgentStep
            {
                Id = Guid.NewGuid(),
                RunId = run.Id,
                Seq = await NextStepSeqAsync(run.Id),
                Kind = "final",
                Status = "failed",
                Title = "AI-run завершился ошибкой",
                Summary = errorMessage,
                ErrorJson = errorRaw,
                CreatedAtUtc = now,
                FinishedAtUtc = now,
                IsVisibleToUser = true,
            });

            await _db.SaveChangesAsync();

            await BroadcastAsync(run.ConversationId, "message.created", new { message = ToMessagePayload(message) });
            await BroadcastAsync(run.ConversationId, "run.failed", new { runId = run.Id, status = run.Status, error = ParseJson(errorRaw) });
            return Ok(new { ok = true, runId = run.Id });
        }

        private async Task<object> BuildWorkerJobAsync(Guid runId)
        {
            var run = await _db.AgentRuns
                .AsNoTracking()
                .Include(x => x.Conversation)
                .FirstAsync(x => x.Id == runId);

            var conversation = run.Conversation;
            var messages = await _db.AgentMessages.AsNoTracking()
                .Where(x => x.ConversationId == conversation.Id)
                .OrderByDescending(x => x.CreatedAtUtc)
                .Take(24)
                .OrderBy(x => x.CreatedAtUtc)
                .Select(x => new { id = x.Id, role = x.Role, text = x.Text, createdAtUtc = x.CreatedAtUtc })
                .ToListAsync();

            var lastUserText = messages.LastOrDefault(x => x.role == "user")?.text ?? ExtractRequestRawText(run.RequestJson) ?? string.Empty;

            object? course = null;
            List<object> assignments = new();
            if (conversation.CourseId.HasValue)
            {
                course = await _db.Courses.AsNoTracking()
                    .Where(x => x.Id == conversation.CourseId.Value)
                    .Select(x => new { id = x.Id, title = x.Title, description = x.Description, isPublic = x.IsPublic })
                    .FirstOrDefaultAsync();

                var assignmentRows = await _db.TaskAssignments.AsNoTracking()
                    .Where(x => x.CourseId == conversation.CourseId.Value)
                    .OrderBy(x => x.Sort)
                    .ThenBy(x => x.CreatedAt)
                    .Select(x => new
                    {
                        id = x.Id,
                        title = x.Title,
                        description = x.Description,
                        type = x.Type,
                        difficulty = x.Difficulty,
                        rating = x.Rating,
                        tags = x.Tags,
                        sort = x.Sort,
                        allowedLanguages = x.AllowedLanguagesCsv,
                    })
                    .ToListAsync();
                assignments = assignmentRows.Cast<object>().ToList();
            }

            var payload = new
            {
                type = "assistant_chat_turn",
                runId = run.Id,
                conversationId = conversation.Id,
                userId = conversation.UserId,
                courseId = conversation.CourseId,
                assignmentId = conversation.AssignmentId,
                supportTicketId = conversation.SupportTicketId,
                rawText = lastUserText,
                message = new { text = lastUserText },
                course,
                assignments,
                recentMessages = messages,
                memory = ParseJson(conversation.MemoryJson ?? "{}"),
                request = ParseJson(run.RequestJson ?? "{}"),
            };

            return new
            {
                id = run.Id,
                type = "assistant_chat_turn",
                jobType = "assistant_chat_turn",
                payload,
            };
        }

        private bool IsAuthorized()
        {
            var expected = _config["TASKFORGE_AGENT_INTERNAL_KEY"]
                           ?? _config["Agent:InternalKey"]
                           ?? _config["API_INTERNAL_KEY"];
            var header = Request.Headers["X-Internal-Key"].ToString();
            return !string.IsNullOrWhiteSpace(expected) && !string.IsNullOrWhiteSpace(header) && header == expected;
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

        private static string CleanWorkerId(string? value)
        {
            var text = string.IsNullOrWhiteSpace(value) ? "taskforge-ai-worker" : value.Trim();
            return text.Length > 128 ? text[..128] : text;
        }

        private static string? ExtractRequestRawText(string? requestJson)
        {
            var doc = ParseJson(requestJson ?? "{}");
            if (doc is JsonElement el)
                return GetString(el, "rawText", "raw_text", "text");
            return null;
        }

        private static IEnumerable<AgentRunArtifact> ExtractArtifacts(Guid runId, JsonElement result, DateTime now)
        {
            if (result.ValueKind != JsonValueKind.Object) yield break;
            if (!result.TryGetProperty("artifacts", out var artifacts) || artifacts.ValueKind != JsonValueKind.Array) yield break;

            foreach (var item in artifacts.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var type = GetString(item, "type") ?? "artifact";
                var title = GetString(item, "title") ?? GetString(item, "scenarioId", "scenario_id") ?? type;
                var dataRaw = GetRaw(item, "data") ?? item.GetRawText();
                yield return new AgentRunArtifact
                {
                    Id = Guid.NewGuid(),
                    RunId = runId,
                    Type = Trim(type, 80),
                    Title = Trim(title, 220),
                    DataJson = string.IsNullOrWhiteSpace(dataRaw) ? "{}" : dataRaw,
                    CreatedAtUtc = now,
                };
            }
        }

        private static bool IsTerminalStepStatus(string? status)
        {
            var value = status ?? string.Empty;
            return value.StartsWith("completed", StringComparison.OrdinalIgnoreCase)
                   || value.Equals("failed", StringComparison.OrdinalIgnoreCase)
                   || value.Equals("canceled", StringComparison.OrdinalIgnoreCase);
        }

        private static string HumanizeStep(string kind, string status)
        {
            return kind switch
            {
                "scenario_result" => "Сценарий собран",
                "route" => "Выбран сценарий",
                "context" => "Контекст курса загружен",
                "final" => status == "failed" ? "AI-run завершился ошибкой" : "AI-run завершён",
                _ => "AI делает следующий шаг",
            };
        }

        private static object? ParseJson(string? json)
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

        private static string? GetString(JsonElement element, params string[] names)
        {
            if (element.ValueKind != JsonValueKind.Object) return null;
            foreach (var name in names)
            {
                if (!element.TryGetProperty(name, out var prop)) continue;
                if (prop.ValueKind == JsonValueKind.String) return prop.GetString();
                if (prop.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False) return prop.ToString();
            }
            return null;
        }

        private static string? GetRaw(JsonElement element, params string[] names)
        {
            if (element.ValueKind != JsonValueKind.Object) return null;
            foreach (var name in names)
            {
                if (element.TryGetProperty(name, out var prop)) return prop.GetRawText();
            }
            return null;
        }

        private static object ToStepPayload(AgentStep step) => new
        {
            id = step.Id,
            runId = step.RunId,
            seq = step.Seq,
            kind = step.Kind,
            status = step.Status,
            actionName = step.ActionName,
            title = step.Title,
            summary = step.Summary,
            output = ParseJson(step.OutputJson),
            error = ParseJson(step.ErrorJson),
            isVisibleToUser = step.IsVisibleToUser,
            createdAtUtc = step.CreatedAtUtc,
            startedAtUtc = step.StartedAtUtc,
            finishedAtUtc = step.FinishedAtUtc,
        };

        private static object ToMessagePayload(AgentMessage message) => new
        {
            id = message.Id,
            conversationId = message.ConversationId,
            runId = message.RunId,
            role = message.Role,
            text = message.Text,
            source = message.Source,
            data = ParseJson(message.DataJson),
            clientMessageId = message.ClientMessageId,
            createdAtUtc = message.CreatedAtUtc,
        };

        private static string Trim(string value, int max)
        {
            value = value.Trim();
            return value.Length <= max ? value : value[..max];
        }
    }
}
