using System.Text;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using taskforge.Data;
using taskforge.Data.Models.DTO;
using taskforge.Data.Models.DTO.Agent;
using taskforge.Data.Models.Entities;
using taskforge.Hubs;
using taskforge.Services.Interfaces;
using taskforge.Services.Files;

namespace taskforge.Controllers.Agent
{
    [ApiController]
    [Route("api/internal/agent")]
    [AllowAnonymous]
    public sealed class InternalAgentController : ControllerBase
    {
        private const int AgentStepKindMaxLength = 64;
        private const int AgentStepStatusMaxLength = 64;
        private const int AgentStepActionNameMaxLength = 128;
        private const int AgentStepTitleMaxLength = 240;
        private const int AgentStepSummaryMaxLength = 1900;

        private readonly ApplicationDbContext _db;
        private readonly IConfiguration _config;
        private readonly IHubContext<AgentHub> _hub;
        private readonly ICompilerService _compiler;
        private readonly IFileStorageService _store;
        private readonly ICourseAccessService _courseAccess;

        public InternalAgentController(ApplicationDbContext db, IConfiguration config, IHubContext<AgentHub> hub, ICompilerService compiler, IFileStorageService store, ICourseAccessService courseAccess)
        {
            _db = db;
            _config = config;
            _hub = hub;
            _compiler = compiler;
            _store = store;
            _courseAccess = courseAccess;
        }

        [HttpPost("claim-next")]
        public async Task<IActionResult> ClaimNext([FromBody] InternalAgentClaimNextRequest request)
        {
            if (!IsAuthorized()) return Unauthorized();
            if (request == null) return BadRequest(new { ok = false, message = "Request body is required." });

            var now = DateTime.UtcNow;
            var workerId = CleanWorkerId(request.WorkerId);

            var candidateId = await _db.AgentRuns
                .Where(x =>
                    x.Status == "queued" ||
                    (x.Status == "sleeping" && (x.NextWakeAtUtc == null || x.NextWakeAtUtc <= now)) ||
                    (x.Status == "running" && x.LeaseExpiresAtUtc != null && x.LeaseExpiresAtUtc <= now))
                .OrderByDescending(x => x.Priority)
                .ThenBy(x => x.CreatedAtUtc)
                .Select(x => (Guid?)x.Id)
                .FirstOrDefaultAsync();

            if (candidateId == null) return Ok(new { job = (object?)null });

            var claimedRows = await _db.AgentRuns
                .Where(x => x.Id == candidateId.Value && (
                    x.Status == "queued" ||
                    (x.Status == "sleeping" && (x.NextWakeAtUtc == null || x.NextWakeAtUtc <= now)) ||
                    (x.Status == "running" && x.LeaseExpiresAtUtc != null && x.LeaseExpiresAtUtc <= now)))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.Status, "running")
                    .SetProperty(x => x.WorkerId, workerId)
                    .SetProperty(x => x.Attempt, x => x.Attempt + 1)
                    .SetProperty(x => x.StartedAtUtc, x => x.StartedAtUtc ?? now)
                    .SetProperty(x => x.UpdatedAtUtc, now)
                    .SetProperty(x => x.LeaseExpiresAtUtc, now.AddSeconds(90)));

            if (claimedRows == 0) return Ok(new { job = (object?)null });

            var run = await _db.AgentRuns
                .Include(x => x.Conversation)
                .FirstOrDefaultAsync(x => x.Id == candidateId.Value);

            if (run == null) return Ok(new { job = (object?)null });

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

            await SaveChangesWithAgentStepSeqRetryAsync(run.Id);

            var job = await BuildWorkerJobAsync(run.Id);
            await BroadcastAsync(run.ConversationId, "run.updated", new { runId = run.Id, status = run.Status, workerId });
            await BroadcastAsync(run.ConversationId, "step.created", new { runId = run.Id, title = "AI-worker начал работу", status = "running" });

            return Ok(new { job });
        }

        [HttpPost("runs/{runId:guid}/heartbeat")]
        public async Task<IActionResult> Heartbeat([FromRoute] Guid runId, [FromBody] InternalAgentHeartbeatRequest request)
        {
            if (!IsAuthorized()) return Unauthorized();
            if (request == null) return BadRequest(new { ok = false, message = "Request body is required." });

            var run = await _db.AgentRuns.FirstOrDefaultAsync(x => x.Id == runId);
            if (run == null) return NotFound();

            var now = DateTime.UtcNow;
            var workerCheck = ValidateActiveWorker(run, request.WorkerId, now);
            if (workerCheck != null) return workerCheck;

            run.LeaseExpiresAtUtc = now.AddSeconds(90);
            run.UpdatedAtUtc = now;
            await _db.SaveChangesAsync();
            return Ok(new { ok = true, runId = run.Id, leaseExpiresAtUtc = run.LeaseExpiresAtUtc });
        }

        [HttpPost("runs/{runId:guid}/steps")]
        public async Task<IActionResult> AppendStep([FromRoute] Guid runId, [FromBody] InternalAgentAppendStepRequest request)
        {
            if (!IsAuthorized()) return Unauthorized();
            if (request == null) return BadRequest(new { ok = false, message = "Request body is required." });

            var run = await _db.AgentRuns.FirstOrDefaultAsync(x => x.Id == runId);
            if (run == null) return NotFound();

            var now = DateTime.UtcNow;
            var workerCheck = ValidateActiveWorker(run, request.WorkerId, now);
            if (workerCheck != null) return workerCheck;

            var step = request.Step;
            var kind = LimitDbText(GetString(step, "kind") ?? "worker", AgentStepKindMaxLength) ?? "worker";
            var status = LimitDbText(GetString(step, "status") ?? "completed", AgentStepStatusMaxLength) ?? "completed";
            var title = LimitDbText(GetString(step, "title", "label", "actionName", "scenarioId") ?? HumanizeStep(kind, status), AgentStepTitleMaxLength) ?? HumanizeStep(kind, status);
            var summary = LimitDbText(GetString(step, "summary", "message", "assistant_message", "reason"), AgentStepSummaryMaxLength);
            var actionName = LimitDbText(GetString(step, "actionName", "action", "scenarioId", "scenario_id"), AgentStepActionNameMaxLength);

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
            run.LeaseExpiresAtUtc = now.AddSeconds(90);
            await SaveChangesWithAgentStepSeqRetryAsync(run.Id);

            await BroadcastAsync(run.ConversationId, "step.created", new { step = ToStepPayload(entity) });
            return Ok(new { ok = true, stepId = entity.Id });
        }

        [HttpPost("runs/{runId:guid}/complete")]
        public async Task<IActionResult> Complete([FromRoute] Guid runId, [FromBody] InternalAgentCompleteRunRequest request)
        {
            if (!IsAuthorized()) return Unauthorized();
            if (request == null) return BadRequest(new { ok = false, message = "Request body is required." });

            var run = await _db.AgentRuns
                .Include(x => x.Conversation)
                .FirstOrDefaultAsync(x => x.Id == runId);
            if (run == null) return NotFound();
            if (run.FinishedAtUtc.HasValue || IsTerminalRunStatus(run.Status))
                return Ok(new { ok = true, runId = run.Id, alreadyCompleted = true, status = run.Status });

            var now = DateTime.UtcNow;
            var workerCheck = ValidateTerminalWorker(run, request.WorkerId, now, "complete");
            if (workerCheck != null) return workerCheck;

            var result = request.Result;
            var rawResult = result.ValueKind == JsonValueKind.Undefined ? "{}" : result.GetRawText();
            var assistantText = GetString(result, "assistantMessage", "assistant_message", "summary")
                                ?? "Готово. Я подготовил результат и приложил его к этому AI-run.";
            var scenarioId = GetString(result, "scenarioId", "scenario_id") ?? run.ScenarioId;
            var status = GetString(result, "status") ?? "completed";

            run.Status = status.StartsWith("completed", StringComparison.OrdinalIgnoreCase) ? status : "completed";
            run.ScenarioId = scenarioId;
            run.ResultJson = rawResult;
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

            var nextStepSeq = await NextStepSeqAsync(run.Id);

            var artifacts = ExtractArtifacts(run.Id, result, now).ToList();
            foreach (var artifact in artifacts)
                _db.AgentRunArtifacts.Add(artifact);

            _db.AgentSteps.Add(new AgentStep
            {
                Id = Guid.NewGuid(),
                RunId = run.Id,
                Seq = nextStepSeq++,
                Kind = "debug",
                Status = "completed",
                ActionName = "worker_result_ingested",
                Title = "AI-result принят backend'ом",
                Summary = $"Размер resultJson: {rawResult.Length} символов. Artifact'ов: {artifacts.Count}.",
                InputJson = JsonSerializer.Serialize(new
                {
                    workerId = request.WorkerId,
                    scenarioId,
                    status,
                    rawResultLength = rawResult.Length,
                    assistantTextLength = assistantText.Length,
                    resultSha256 = Sha256Text(rawResult)
                }),
                OutputJson = JsonSerializer.Serialize(new
                {
                    artifacts = artifacts.Select(x => new
                    {
                        x.Id,
                        x.Type,
                        x.Title,
                        dataJsonLength = x.DataJson?.Length ?? 0,
                        x.ContentHash,
                        x.StorageKey
                    }).ToList()
                }),
                CreatedAtUtc = now,
                FinishedAtUtc = now,
                IsVisibleToUser = false,
            });

            var createdDrafts = new List<object>();
            var rejectedDraftArtifacts = new List<object>();
            foreach (var artifact in artifacts)
            {
                var created = await TryCreateHiddenDraftAssignmentAsync(run, artifact, now);
                if (created != null)
                    createdDrafts.Add(created);
                else if (IsHiddenDraftArtifactType(artifact.Type))
                    rejectedDraftArtifacts.Add(new { artifact.Id, artifact.Type, artifact.Title, dataJsonLength = artifact.DataJson?.Length ?? 0 });
            }

            if (rejectedDraftArtifacts.Count > 0)
            {
                _db.AgentSteps.Add(new AgentStep
                {
                    Id = Guid.NewGuid(),
                    RunId = run.Id,
                    Seq = nextStepSeq++,
                    Kind = "debug",
                    Status = "failed",
                    ActionName = "hidden_draft_not_created",
                    Title = "AI-черновик не прошёл backend-gate",
                    Summary = "Backend не создал скрытый draft: artifact не подходит под правила создания, права, placement или runner validation.",
                    OutputJson = JsonSerializer.Serialize(new { rejectedDraftArtifacts }),
                    CreatedAtUtc = now,
                    FinishedAtUtc = now,
                    IsVisibleToUser = false,
                });
            }

            if (createdDrafts.Count > 0)
            {
                _db.AgentSteps.Add(new AgentStep
                {
                    Id = Guid.NewGuid(),
                    RunId = run.Id,
                    Seq = nextStepSeq++,
                    Kind = "draft",
                    Status = "completed",
                    ActionName = "create_hidden_assignment_draft",
                    Title = "Создан скрытый черновик задания",
                    Summary = $"Создано скрытых AI-черновиков: {createdDrafts.Count}.",
                    OutputJson = JsonSerializer.Serialize(new { createdDrafts }),
                    CreatedAtUtc = now,
                    FinishedAtUtc = now,
                    IsVisibleToUser = true,
                });
            }

            var courseEditProposals = artifacts
                .Where(x => string.Equals(x.Type, "course_edit_proposal", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(x.Type, "assignment_update_batch", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(x.Type, "course_style_update", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(x.Type, "approval_request", StringComparison.OrdinalIgnoreCase))
                .Select(x => new { x.Id, x.Type, x.Title })
                .ToList();

            if (courseEditProposals.Count > 0)
            {
                _db.AgentSteps.Add(new AgentStep
                {
                    Id = Guid.NewGuid(),
                    RunId = run.Id,
                    Seq = nextStepSeq++,
                    Kind = "course_edit",
                    Status = "waiting_approval",
                    ActionName = "course_edit_proposal",
                    Title = "AI подготовил правки курса",
                    Summary = "Правки сохранены как proposal artifact и не применяются автоматически. Их должен подтвердить пользователь с правом редактирования курса.",
                    OutputJson = JsonSerializer.Serialize(new { courseEditProposals }),
                    CreatedAtUtc = now,
                    FinishedAtUtc = now,
                    IsVisibleToUser = true,
                });
            }

            _db.AgentSteps.Add(new AgentStep
            {
                Id = Guid.NewGuid(),
                RunId = run.Id,
                Seq = nextStepSeq++,
                Kind = "final",
                Status = "completed",
                ActionName = scenarioId,
                Title = "AI закончил ответ",
                Summary = LimitDbText(assistantText, AgentStepSummaryMaxLength),
                OutputJson = rawResult,
                CreatedAtUtc = now,
                FinishedAtUtc = now,
                IsVisibleToUser = true,
            });

            var memoryPatchRaw = GetRaw(result, "memoryPatch", "memory_patch");
            if (!string.IsNullOrWhiteSpace(memoryPatchRaw))
                run.Conversation.MemoryJson = MergeAgentMemoryPatch(run.Conversation.MemoryJson, memoryPatchRaw);

            if (!run.Conversation.CourseId.HasValue)
            {
                var createdCourseId = ExtractFirstCreatedDraftCourseId(createdDrafts);
                if (createdCourseId.HasValue)
                    run.Conversation.CourseId = createdCourseId.Value;
                else
                {
                    var activeCourseId = ExtractActiveCourseIdFromMemory(run.Conversation.MemoryJson);
                    if (activeCourseId.HasValue)
                        run.Conversation.CourseId = activeCourseId.Value;
                }
            }

            run.Conversation.UpdatedAtUtc = now;
            await SaveChangesWithAgentStepSeqRetryAsync(run.Id);

            await BroadcastAsync(run.ConversationId, "message.created", new { message = ToMessagePayload(message) });
            await BroadcastAsync(run.ConversationId, "run.completed", new { runId = run.Id, status = run.Status, result = ParseJson(rawResult) });
            return Ok(new { ok = true, runId = run.Id });
        }

        [HttpPost("runs/{runId:guid}/fail")]
        public async Task<IActionResult> Fail([FromRoute] Guid runId, [FromBody] InternalAgentFailRunRequest request)
        {
            if (!IsAuthorized()) return Unauthorized();
            if (request == null) return BadRequest(new { ok = false, message = "Request body is required." });

            var run = await _db.AgentRuns.Include(x => x.Conversation).FirstOrDefaultAsync(x => x.Id == runId);
            if (run == null) return NotFound();
            if (run.FinishedAtUtc.HasValue || IsTerminalRunStatus(run.Status))
                return Ok(new { ok = true, runId = run.Id, alreadyCompleted = true, status = run.Status });

            var now = DateTime.UtcNow;
            var workerCheck = ValidateTerminalWorker(run, request.WorkerId, now, "fail");
            if (workerCheck != null) return workerCheck;

            var errorRaw = request.Error.ValueKind == JsonValueKind.Undefined ? "{}" : request.Error.GetRawText();
            var errorMessage = GetString(request.Error, "message", "error") ?? "AI-worker не смог завершить задачу.";

            run.Status = "failed";
            run.ErrorJson = errorRaw;
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
                Summary = LimitDbText(errorMessage, AgentStepSummaryMaxLength),
                ErrorJson = errorRaw,
                CreatedAtUtc = now,
                FinishedAtUtc = now,
                IsVisibleToUser = true,
            });

            await SaveChangesWithAgentStepSeqRetryAsync(run.Id);

            await BroadcastAsync(run.ConversationId, "message.created", new { message = ToMessagePayload(message) });
            await BroadcastAsync(run.ConversationId, "run.failed", new { runId = run.Id, status = run.Status, error = ParseJson(errorRaw) });
            return Ok(new { ok = true, runId = run.Id });
        }


        [HttpPost("tools/run-tests")]
        public async Task<IActionResult> RunTestsTool([FromBody] TestRunRequestDto request)
        {
            if (!IsAuthorized()) return Unauthorized();
            if (request == null)
                return BadRequest(new { ok = false, message = "Request body is required." });
            if (request.TestCases == null || request.TestCases.Count == 0)
                return BadRequest(new { ok = false, message = "Field 'testCases' must contain at least one test case." });
            if (!request.RunId.HasValue)
                return BadRequest(new { ok = false, message = "Field 'runId' is required for internal AI test runs." });

            var run = await _db.AgentRuns.FirstOrDefaultAsync(x => x.Id == request.RunId.Value);
            if (run == null) return NotFound(new { ok = false, message = "AI run not found." });
            var now = DateTime.UtcNow;
            var workerCheck = ValidateActiveWorker(run, request.WorkerId, now);
            if (workerCheck != null) return workerCheck;

            request.Language = NormalizeRunnerLanguage(request.Language);
            IList<TestResultDto> results;
            try
            {
                results = await _compiler.RunTestsAsync(request);
            }
            catch (Exception ex)
            {
                _db.AgentSteps.Add(new AgentStep
                {
                    Id = Guid.NewGuid(),
                    RunId = run.Id,
                    Seq = await NextStepSeqAsync(run.Id),
                    Kind = "runner",
                    Status = "failed",
                    ActionName = "internal_run_tests",
                    Title = "AI runner-запрос упал",
                    Summary = ex.Message,
                    InputJson = JsonSerializer.Serialize(new
                    {
                        request.RunId,
                        request.WorkerId,
                        request.Language,
                        codeLength = request.Code?.Length ?? 0,
                        codeSha256 = Sha256Text(request.Code ?? string.Empty),
                        testCount = request.TestCases?.Count ?? 0,
                        request.TimeLimitMs,
                        request.MemoryLimitMb,
                        forbiddenPolicyCount = request.PolicyForbiddenCalls?.Count ?? 0,
                        requiredPolicyCount = request.PolicyRequiredCalls?.Count ?? 0
                    }),
                    ErrorJson = JsonSerializer.Serialize(new { ex.Message, ex.StackTrace }),
                    CreatedAtUtc = now,
                    FinishedAtUtc = now,
                    IsVisibleToUser = false,
                });
                await SaveChangesWithAgentStepSeqRetryAsync(run.Id);
                return StatusCode(500, new { ok = false, message = "Runner не смог проверить AI-код.", error = ex.Message });
            }

            var ok = results.Count > 0 && results.All(x => x.Passed && string.Equals(x.Status, "ok", StringComparison.OrdinalIgnoreCase));
            var response = new { ok, results };
            _db.AgentSteps.Add(new AgentStep
            {
                Id = Guid.NewGuid(),
                RunId = run.Id,
                Seq = await NextStepSeqAsync(run.Id),
                Kind = "runner",
                Status = ok ? "completed" : "failed",
                ActionName = "internal_run_tests",
                Title = ok ? "AI проверил решение через runner" : "AI runner-проверка не прошла",
                Summary = $"Язык: {request.Language}. Тестов: {request.TestCases?.Count ?? 0}. Пройдено: {results.Count(x => x.Passed)}/{results.Count}.",
                InputJson = JsonSerializer.Serialize(new
                {
                    request.RunId,
                    request.WorkerId,
                    request.Language,
                    codeLength = request.Code?.Length ?? 0,
                    codeSha256 = Sha256Text(request.Code ?? string.Empty),
                    tests = request.TestCases,
                    request.TimeLimitMs,
                    request.MemoryLimitMb,
                    request.PolicyForbiddenCalls,
                    request.PolicyRequiredCalls
                }),
                OutputJson = JsonSerializer.Serialize(response),
                CreatedAtUtc = now,
                FinishedAtUtc = now,
                IsVisibleToUser = false,
            });
            run.UpdatedAtUtc = now;
            run.LeaseExpiresAtUtc = now.AddSeconds(90);
            await SaveChangesWithAgentStepSeqRetryAsync(run.Id);
            await BroadcastAsync(run.ConversationId, "step.created", new { runId = run.Id, title = "AI runner-проверка", status = ok ? "completed" : "failed" });
            return Ok(response);
        }

        private async Task<object> BuildWorkerJobAsync(Guid runId)
        {
            var run = await _db.AgentRuns
                .AsNoTracking()
                .Include(x => x.Conversation)
                .FirstAsync(x => x.Id == runId);

            var conversation = run.Conversation;
            var messageRows = await _db.AgentMessages.AsNoTracking()
                .Where(x => x.ConversationId == conversation.Id)
                .OrderByDescending(x => x.CreatedAtUtc)
                .Take(24)
                .OrderBy(x => x.CreatedAtUtc)
                .Select(x => new { id = x.Id, role = x.Role, text = x.Text, createdAtUtc = x.CreatedAtUtc, dataJson = x.DataJson })
                .ToListAsync();

            var messages = messageRows
                .Select(x => new
                {
                    id = x.id,
                    role = x.role,
                    text = x.text,
                    createdAtUtc = x.createdAtUtc,
                    data = ParseJson(x.dataJson),
                    attachments = ExtractAgentAttachments(x.dataJson)
                })
                .ToList();

            var requestJsonObj = ParseJson(run.RequestJson ?? "{}");
            var requestJson = requestJsonObj is JsonElement reqEl ? reqEl : default;
            var allAttachments = messages.SelectMany(x => x.attachments).Concat(ExtractAgentAttachments(requestJson)).ToList();
            var fileContexts = await BuildAgentFileContextsAsync(allAttachments);

            var lastUserText = messages.LastOrDefault(x => x.role == "user")?.text ?? ExtractRequestRawText(run.RequestJson) ?? string.Empty;
            var normalizedUserText = NormalizeCourseSearchText(lastUserText);
            var conversationSearchText = NormalizeCourseSearchText(string.Join(" ", new[]
            {
                conversation.Title,
                string.Join(" ", messages.Where(x => x.role == "user").Select(x => x.text)),
                ExtractRequestRawText(run.RequestJson),
            }.Where(x => !string.IsNullOrWhiteSpace(x))));
            var memoryCourseIds = ExtractCourseIdsFromAgentMemory(conversation.MemoryJson).ToHashSet();
            var targetConcepts = InferTargetConcepts($"{normalizedUserText} {conversationSearchText}");

            object? selectedCourse = null;
            List<AssignmentContextRow> selectedAssignmentRows = new();
            if (conversation.CourseId.HasValue)
            {
                selectedCourse = await _db.Courses.AsNoTracking()
                    .Where(x => x.Id == conversation.CourseId.Value)
                    .Select(x => new { id = x.Id, title = x.Title, description = x.Description, isPublic = x.IsPublic })
                    .FirstOrDefaultAsync();

                selectedAssignmentRows = await LoadAssignmentRowsAsync(new[] { conversation.CourseId.Value });
            }

            var courseRows = await _db.Courses.AsNoTracking()
                .OrderBy(x => x.Title)
                .Select(x => new
                {
                    id = x.Id,
                    title = x.Title,
                    description = x.Description,
                    isPublic = x.IsPublic,
                    assignmentCount = _db.TaskAssignments.Count(a => a.CourseId == x.Id)
                })
                .ToListAsync();

            var courseCatalog = courseRows.Cast<object>().ToList();
            var lowerText = normalizedUserText;
            var wantsWideContext = lowerText.Contains("все курсы") || lowerText.Contains("любой курс") || lowerText.Contains("любые курсы") || lowerText.Contains("курсы") || !conversation.CourseId.HasValue;

            var scoredCourses = courseRows
                .Select(x => new
                {
                    x.id,
                    x.title,
                    x.description,
                    x.assignmentCount,
                    Score = ScoreCourseForAgentContext(lowerText, x.title, x.description)
                            + Math.Min(70, ScoreCourseForAgentContext(conversationSearchText, x.title, x.description) / 2)
                            + (memoryCourseIds.Contains(x.id) ? 75 : 0)
                })
                .OrderByDescending(x => x.Score)
                .ThenByDescending(x => x.assignmentCount)
                .ThenBy(x => x.title)
                .ToList();

            var strongMatchedCourses = scoredCourses
                .Where(x => x.Score >= 45)
                .Take(4)
                .ToList();

            var effectiveCourseId = conversation.CourseId ?? strongMatchedCourses.FirstOrDefault()?.id;
            if (!conversation.CourseId.HasValue && effectiveCourseId.HasValue)
            {
                selectedCourse = await _db.Courses.AsNoTracking()
                    .Where(x => x.Id == effectiveCourseId.Value)
                    .Select(x => new { id = x.Id, title = x.Title, description = x.Description, isPublic = x.IsPublic })
                    .FirstOrDefaultAsync();

                selectedAssignmentRows = await LoadAssignmentRowsAsync(new[] { effectiveCourseId.Value });
            }

            var contextTake = strongMatchedCourses.Count > 0 ? Math.Max(4, strongMatchedCourses.Count) : (wantsWideContext ? 12 : 6);
            var contextCourseIds = (strongMatchedCourses.Count > 0 ? strongMatchedCourses : scoredCourses)
                .Take(contextTake)
                .Select(x => x.id)
                .ToList();

            if (effectiveCourseId.HasValue && !contextCourseIds.Contains(effectiveCourseId.Value))
                contextCourseIds.Insert(0, effectiveCourseId.Value);

            if (contextCourseIds.Count == 0)
                contextCourseIds = courseRows.Take(12).Select(x => x.id).ToList();

            var contextAssignmentRows = await LoadAssignmentRowsAsync(contextCourseIds);
            var courseById = courseRows.ToDictionary(x => x.id, x => x);

            var selectedOutline = selectedAssignmentRows
                .Select((a, index) => BuildAssignmentOutline(a, index, includeDescriptionPreview: true))
                .Cast<object>()
                .ToList();

            var focusAssignments = BuildFocusAssignments(selectedAssignmentRows, targetConcepts)
                .Select((a, index) => BuildAssignmentDetail(a, index))
                .Cast<object>()
                .ToList();

            var courseDigest = BuildCourseDigestPayload(
                effectiveCourseId,
                selectedCourse,
                selectedAssignmentRows,
                selectedOutline,
                targetConcepts,
                focusAssignments.Count,
                courseCatalog.Count);

            var editableAssignments = await BuildEditableAssignmentsAsync(effectiveCourseId);

            var compactContextRows = contextAssignmentRows
                .Select((a, index) => BuildAssignmentOutline(a, index, includeDescriptionPreview: false))
                .ToList();

            var courseContexts = contextCourseIds
                .Where(courseById.ContainsKey)
                .Select(id =>
                {
                    var c = courseById[id];
                    return new
                    {
                        course = new { id = c.id, title = c.title, description = c.description, isPublic = c.isPublic, assignmentCount = c.assignmentCount },
                        assignments = compactContextRows.Where(a => a.CourseId == c.id).Cast<object>().ToList()
                    };
                })
                .Cast<object>()
                .ToList();

            var matchedCourses = scoredCourses
                .Where(x => x.Score > 0)
                .Take(8)
                .Select(x => new { id = x.id, title = x.title, score = x.Score, assignmentCount = x.assignmentCount })
                .Cast<object>()
                .ToList();

            var payload = new
            {
                type = run.ScenarioId,
                jobType = run.ScenarioId,
                scenarioId = run.ScenarioId,
                runId = run.Id,
                conversationId = conversation.Id,
                userId = conversation.UserId,
                courseId = effectiveCourseId,
                assignmentId = conversation.AssignmentId,
                supportTicketId = conversation.SupportTicketId,
                rawText = lastUserText,
                message = new { text = lastUserText },
                course = selectedCourse,
                targetConcepts,
                attachments = fileContexts,
                fileContexts,
                focusAssignments,
                targetAssignments = focusAssignments,
                courseOutline = selectedOutline,
                courseDigest,
                editableAssignments,
                assignments = focusAssignments,
                matchedCourses,
                courseCatalog,
                courseContexts,
                recentMessages = messages,
                memory = ParseJson(conversation.MemoryJson ?? "{}"),
                request = ParseJson(run.RequestJson ?? "{}"),
            };

            return new
            {
                id = run.Id,
                type = run.ScenarioId,
                jobType = run.ScenarioId,
                scenarioId = run.ScenarioId,
                payload,
            };
        }

        private sealed class AssignmentContextRow
        {
            public Guid Id { get; init; }
            public Guid CourseId { get; init; }
            public string Title { get; init; } = string.Empty;
            public string Description { get; init; } = string.Empty;
            public string Type { get; init; } = string.Empty;
            public int Difficulty { get; init; }
            public int Rating { get; init; }
            public string? Tags { get; init; }
            public int Sort { get; init; }
            public string? AllowedLanguages { get; init; }
            public DateTime CreatedAt { get; init; }
            public bool IsHidden { get; init; }
            public string LifecycleStatus { get; init; } = "published";
            public bool IsAiDraft { get; init; }
            public int TestQuestionCount { get; set; }
            public int MathBlockCount { get; set; }
            public List<string> TestQuestionPreviews { get; set; } = new();
            public List<string> MathBlockPreviews { get; set; } = new();
        }

        private sealed class AssignmentOutlineRow
        {
            public Guid Id { get; init; }
            public Guid CourseId { get; init; }
            public string Title { get; init; } = string.Empty;
            public string Type { get; init; } = string.Empty;
            public int Difficulty { get; init; }
            public int Rating { get; init; }
            public string? Tags { get; init; }
            public int Sort { get; init; }
            public int Index { get; init; }
            public string? AllowedLanguages { get; init; }
            public string DescriptionPreview { get; init; } = string.Empty;
            public List<string> ConceptHints { get; init; } = new();
            public object? ContentSummary { get; init; }
            public bool IsHidden { get; init; }
            public string LifecycleStatus { get; init; } = "published";
            public bool IsAiDraft { get; init; }
        }

        private async Task<List<AssignmentContextRow>> LoadAssignmentRowsAsync(IEnumerable<Guid> courseIds)
        {
            var ids = courseIds.Distinct().ToList();
            if (ids.Count == 0) return new List<AssignmentContextRow>();

            var rows = await _db.TaskAssignments.AsNoTracking()
                .Where(x => ids.Contains(x.CourseId))
                .OrderBy(x => x.CourseId)
                .ThenBy(x => x.Sort)
                .ThenBy(x => x.CreatedAt)
                .Select(x => new AssignmentContextRow
                {
                    Id = x.Id,
                    CourseId = x.CourseId,
                    Title = x.Title,
                    Description = x.Description,
                    Type = x.Type,
                    Difficulty = x.Difficulty,
                    Rating = x.Rating,
                    Tags = x.Tags,
                    Sort = x.Sort,
                    AllowedLanguages = x.AllowedLanguagesCsv,
                    CreatedAt = x.CreatedAt,
                    IsHidden = x.IsHidden,
                    LifecycleStatus = x.LifecycleStatus,
                    IsAiDraft = x.IsAiDraft,
                })
                .ToListAsync();

            var assignmentIds = rows.Select(x => x.Id).ToList();
            if (assignmentIds.Count == 0) return rows;

            var testPrompts = await _db.TaskTestQuestions.AsNoTracking()
                .Where(x => assignmentIds.Contains(x.TaskAssignmentId))
                .OrderBy(x => x.TaskAssignmentId)
                .ThenBy(x => x.Order)
                .Select(x => new { x.TaskAssignmentId, x.Prompt })
                .ToListAsync();

            var mathPrompts = await _db.TaskMathBlocks.AsNoTracking()
                .Where(x => assignmentIds.Contains(x.TaskAssignmentId))
                .OrderBy(x => x.TaskAssignmentId)
                .ThenBy(x => x.Order)
                .Select(x => new { x.TaskAssignmentId, x.Prompt })
                .ToListAsync();

            var rowById = rows.ToDictionary(x => x.Id);
            foreach (var group in testPrompts.GroupBy(x => x.TaskAssignmentId))
            {
                if (!rowById.TryGetValue(group.Key, out var row)) continue;
                row.TestQuestionCount = group.Count();
                row.TestQuestionPreviews = group.Select(x => Preview(x.Prompt, 100)).Where(x => !string.IsNullOrWhiteSpace(x)).Take(4).ToList();
            }
            foreach (var group in mathPrompts.GroupBy(x => x.TaskAssignmentId))
            {
                if (!rowById.TryGetValue(group.Key, out var row)) continue;
                row.MathBlockCount = group.Count();
                row.MathBlockPreviews = group.Select(x => Preview(x.Prompt, 100)).Where(x => !string.IsNullOrWhiteSpace(x)).Take(4).ToList();
            }

            return rows;
        }

        private static AssignmentOutlineRow BuildAssignmentOutline(AssignmentContextRow assignment, int index, bool includeDescriptionPreview)
        {
            return new AssignmentOutlineRow
            {
                Id = assignment.Id,
                CourseId = assignment.CourseId,
                Title = assignment.Title,
                Type = assignment.Type,
                Difficulty = assignment.Difficulty,
                Rating = assignment.Rating,
                Tags = assignment.Tags,
                Sort = assignment.Sort,
                Index = index,
                AllowedLanguages = assignment.AllowedLanguages,
                DescriptionPreview = includeDescriptionPreview ? Preview(assignment.Description, 260) : Preview(assignment.Description, 120),
                ConceptHints = DetectAssignmentConceptHints(assignment),
                ContentSummary = BuildAssignmentContentSummary(assignment),
                IsHidden = assignment.IsHidden,
                LifecycleStatus = assignment.LifecycleStatus,
                IsAiDraft = assignment.IsAiDraft,
            };
        }

        private static object BuildAssignmentDetail(AssignmentContextRow assignment, int index)
        {
            return new
            {
                id = assignment.Id,
                courseId = assignment.CourseId,
                title = assignment.Title,
                description = assignment.Description,
                type = assignment.Type,
                difficulty = assignment.Difficulty,
                rating = assignment.Rating,
                tags = assignment.Tags,
                sort = assignment.Sort,
                index,
                allowedLanguages = assignment.AllowedLanguages,
                isHidden = assignment.IsHidden,
                lifecycleStatus = assignment.LifecycleStatus,
                isAiDraft = assignment.IsAiDraft,
                conceptHints = DetectAssignmentConceptHints(assignment),
                contentSummary = BuildAssignmentContentSummary(assignment),
            };
        }

        private static object? BuildAssignmentContentSummary(AssignmentContextRow assignment)
        {
            if (string.Equals(assignment.Type, "test", StringComparison.OrdinalIgnoreCase))
            {
                return new
                {
                    type = "test",
                    questionCount = assignment.TestQuestionCount,
                    questionPreviews = assignment.TestQuestionPreviews
                };
            }
            if (string.Equals(assignment.Type, "math", StringComparison.OrdinalIgnoreCase))
            {
                return new
                {
                    type = "math",
                    blockCount = assignment.MathBlockCount,
                    blockPreviews = assignment.MathBlockPreviews
                };
            }
            return null;
        }

        private async Task<List<object>> BuildEditableAssignmentsAsync(Guid? courseId)
        {
            if (!courseId.HasValue) return new List<object>();
            var assignments = await _db.TaskAssignments.AsNoTracking()
                .Where(x => x.CourseId == courseId.Value)
                .OrderBy(x => x.Sort)
                .ThenBy(x => x.CreatedAt)
                .Select(x => new
                {
                    x.Id,
                    x.CourseId,
                    x.Title,
                    x.Description,
                    x.Type,
                    x.Difficulty,
                    x.Rating,
                    x.Tags,
                    x.Sort,
                    x.AllowedLanguagesCsv,
                    x.IsHidden,
                    x.LifecycleStatus,
                    x.IsAiDraft,
                    x.ImageTestReferenceKey,
                    x.ImageTestSimilarityThreshold
                })
                .ToListAsync();

            var ids = assignments.Select(x => x.Id).ToList();
            var testCases = await _db.TaskTestCases.AsNoTracking()
                .Where(x => ids.Contains(x.TaskAssignmentId))
                .OrderBy(x => x.TaskAssignmentId)
                .ThenBy(x => x.IsHidden)
                .Select(x => new { x.TaskAssignmentId, x.Input, x.ExpectedOutput, x.IsHidden })
                .ToListAsync();
            var testQuestions = await _db.TaskTestQuestions.AsNoTracking()
                .Where(x => ids.Contains(x.TaskAssignmentId))
                .OrderBy(x => x.TaskAssignmentId)
                .ThenBy(x => x.Order)
                .Select(x => new { x.TaskAssignmentId, x.Order, x.Type, x.Prompt, x.DataJson })
                .ToListAsync();
            var testSettings = await _db.TaskTestSettings.AsNoTracking()
                .Where(x => ids.Contains(x.TaskAssignmentId))
                .Select(x => new { x.TaskAssignmentId, x.MaxAttempts, x.PassPercent, x.ShuffleQuestions, x.ShuffleAnswers, x.AllowReview, x.AttemptTimeLimitsJson })
                .ToListAsync();
            var mathBlocks = await _db.TaskMathBlocks.AsNoTracking()
                .Where(x => ids.Contains(x.TaskAssignmentId))
                .OrderBy(x => x.TaskAssignmentId)
                .ThenBy(x => x.Order)
                .Select(x => new { x.TaskAssignmentId, x.Order, x.Kind, x.Prompt, x.PromptContentJson, x.DataJson, x.Score, x.IsRequired })
                .ToListAsync();
            var mathSettings = await _db.TaskMathSettings.AsNoTracking()
                .Where(x => ids.Contains(x.TaskAssignmentId))
                .Select(x => new { x.TaskAssignmentId, x.MaxAttempts, x.PassPercent, x.ShuffleBlocks, x.AllowReview, x.AttemptTimeLimitsJson })
                .ToListAsync();

            var testCaseLookup = testCases.GroupBy(x => x.TaskAssignmentId).ToDictionary(x => x.Key, x => x.ToList());
            var questionLookup = testQuestions.GroupBy(x => x.TaskAssignmentId).ToDictionary(x => x.Key, x => x.ToList());
            var testSettingsLookup = testSettings.ToDictionary(x => x.TaskAssignmentId);
            var blockLookup = mathBlocks.GroupBy(x => x.TaskAssignmentId).ToDictionary(x => x.Key, x => x.ToList());
            var mathSettingsLookup = mathSettings.ToDictionary(x => x.TaskAssignmentId);

            return assignments.Select((a, index) =>
            {
                testSettingsLookup.TryGetValue(a.Id, out var ts);
                mathSettingsLookup.TryGetValue(a.Id, out var ms);
                testCaseLookup.TryGetValue(a.Id, out var cases);
                questionLookup.TryGetValue(a.Id, out var questions);
                blockLookup.TryGetValue(a.Id, out var blocks);
                return (object)new
                {
                    id = a.Id,
                    courseId = a.CourseId,
                    index,
                    sort = a.Sort,
                    title = a.Title,
                    description = a.Description,
                    type = a.Type,
                    difficulty = a.Difficulty,
                    rating = a.Rating,
                    tags = a.Tags,
                    allowedLanguagesCsv = a.AllowedLanguagesCsv,
                    isHidden = a.IsHidden,
                    lifecycleStatus = a.LifecycleStatus,
                    isAiDraft = a.IsAiDraft,
                    imageTestReferenceKey = a.ImageTestReferenceKey,
                    imageTestSimilarityThreshold = a.ImageTestSimilarityThreshold,
                    testCases = cases == null ? new List<object>() : cases.Select(x => (object)new { input = x.Input, expectedOutput = x.ExpectedOutput, isHidden = x.IsHidden }).ToList(),
                    testSpec = questions == null ? null : new
                    {
                        settings = ts == null ? null : new { ts.MaxAttempts, ts.PassPercent, ts.ShuffleQuestions, ts.ShuffleAnswers, ts.AllowReview, ts.AttemptTimeLimitsJson },
                        questions = questions.Select(q => new { q.Order, q.Type, q.Prompt, data = ParseJson(q.DataJson) }).ToList()
                    },
                    mathSpec = blocks == null ? null : new
                    {
                        settings = ms == null ? null : new { ms.MaxAttempts, ms.PassPercent, ms.ShuffleBlocks, ms.AllowReview, ms.AttemptTimeLimitsJson },
                        blocks = blocks.Select(b => new { b.Order, b.Kind, b.Prompt, b.PromptContentJson, data = ParseJson(b.DataJson), b.Score, b.IsRequired }).ToList()
                    }
                };
            }).ToList();
        }

        private static List<AssignmentContextRow> BuildFocusAssignments(List<AssignmentContextRow> assignments, List<string> targetConcepts)
        {
            if (assignments.Count == 0) return new List<AssignmentContextRow>();
            if (targetConcepts.Count == 0) return assignments.Take(24).ToList();

            var hits = assignments
                .Select((assignment, index) => new { assignment, index, score = ScoreAssignmentForConcepts(assignment, targetConcepts) })
                .Where(x => x.score > 0)
                .OrderBy(x => x.index)
                .ToList();

            if (hits.Count == 0)
                return assignments.Take(24).ToList();

            var selected = new SortedSet<int>();
            foreach (var hit in hits.Take(18))
            {
                for (var i = Math.Max(0, hit.index - 5); i <= Math.Min(assignments.Count - 1, hit.index + 7); i++)
                    selected.Add(i);
            }

            selected.Add(0);
            selected.Add(Math.Min(assignments.Count - 1, 1));
            selected.Add(Math.Min(assignments.Count - 1, 2));

            return selected.Take(80).Select(i => assignments[i]).ToList();
        }

        private static object BuildCourseDigestPayload(Guid? effectiveCourseId, object? selectedCourse, List<AssignmentContextRow> assignments, List<object> outline, List<string> targetConcepts, int focusCount, int courseCatalogCount)
        {
            var conceptFirstSeen = new Dictionary<string, Guid>();
            foreach (var assignment in assignments)
            {
                foreach (var concept in DetectAssignmentConceptHints(assignment))
                    conceptFirstSeen.TryAdd(concept, assignment.Id);
            }

            return new
            {
                courseId = effectiveCourseId,
                selectedCourse,
                courseCatalogCount,
                assignmentCount = assignments.Count,
                focusAssignmentCount = focusCount,
                targetConcepts,
                assignments = outline,
                conceptFirstSeen,
                freshness = assignments.Count > 0 ? "payload-derived-full-course-outline" : "no-course-data",
            };
        }

        private static int ScoreAssignmentForConcepts(AssignmentContextRow assignment, List<string> targetConcepts)
        {
            var text = NormalizeCourseSearchText($"{assignment.Title} {assignment.Description} {assignment.Tags} {string.Join(' ', assignment.TestQuestionPreviews)} {string.Join(' ', assignment.MathBlockPreviews)}");
            var titleTags = NormalizeCourseSearchText($"{assignment.Title} {assignment.Tags}");
            var score = 0;
            foreach (var concept in targetConcepts)
            {
                if (concept == "if")
                {
                    if (Regex.IsMatch(text, @"(^|[^a-zа-я0-9_])if([^a-zа-я0-9_]|$)", RegexOptions.IgnoreCase)) score += 80;
                    if (text.Contains("если")) score += 60;
                    if (text.Contains("иначе")) score += 50;
                    if (text.Contains("ветв")) score += 45;
                    if (text.Contains("условный") || text.Contains("условного") || titleTags.Contains("услов")) score += 45;
                }
                if (concept == "comparison")
                {
                    if (text.Contains("сравн")) score += 40;
                    if (text.Contains("больше") || text.Contains("меньше") || text.Contains("равн") || text.Contains("четн")) score += 25;
                    if (text.Contains(">") || text.Contains("<") || text.Contains("==") || text.Contains("!=")) score += 30;
                }
                if (concept == "input")
                {
                    if (text.Contains("cin") || text.Contains("scanf") || text.Contains("ввод") || text.Contains("считай") || text.Contains("дано")) score += 25;
                }
                if (concept == "loops")
                {
                    if (Regex.IsMatch(text, @"(^|[^a-zа-я0-9_])for([^a-zа-я0-9_]|$)", RegexOptions.IgnoreCase)) score += 60;
                    if (Regex.IsMatch(text, @"(^|[^a-zа-я0-9_])while([^a-zа-я0-9_]|$)", RegexOptions.IgnoreCase)) score += 60;
                    if (text.Contains("цикл") || text.Contains("повтор") || text.Contains("итерац")) score += 55;
                }
                if (concept == "arrays")
                {
                    if (text.Contains("массив") || text.Contains("список") || text.Contains("элемент")) score += 55;
                    if (text.Contains("array") || text.Contains("vector") || text.Contains("list") || text.Contains("[]")) score += 60;
                }
            }
            return score;
        }

        private static List<string> InferTargetConcepts(string normalizedMessage)
        {
            var result = new List<string>();
            if (Regex.IsMatch(normalizedMessage, @"(^|[^a-zа-я0-9_])if([^a-zа-я0-9_]|$)", RegexOptions.IgnoreCase)
                || normalizedMessage.Contains("услов")
                || normalizedMessage.Contains("ветв")
                || normalizedMessage.Contains("если"))
            {
                result.Add("if");
                result.Add("comparison");
            }
            if (normalizedMessage.Contains("ввод") || normalizedMessage.Contains("cin") || normalizedMessage.Contains("scanf")) result.Add("input");
            if (normalizedMessage.Contains("цикл") || normalizedMessage.Contains("for") || normalizedMessage.Contains("while")) result.Add("loops");
            if (normalizedMessage.Contains("массив") || normalizedMessage.Contains("array") || normalizedMessage.Contains("vector")) result.Add("arrays");
            return result.Distinct().ToList();
        }

        private static List<string> DetectAssignmentConceptHints(AssignmentContextRow assignment)
        {
            var text = NormalizeCourseSearchText($"{assignment.Title} {assignment.Description} {assignment.Tags} {string.Join(' ', assignment.TestQuestionPreviews)} {string.Join(' ', assignment.MathBlockPreviews)}");
            var titleTags = NormalizeCourseSearchText($"{assignment.Title} {assignment.Tags}");
            var result = new List<string>();
            if (text.Contains("cout") || text.Contains("printf") || text.Contains("вывод") || text.Contains("напечат")) result.Add("output");
            if (text.Contains("cin") || text.Contains("scanf") || text.Contains("ввод") || text.Contains("считай") || text.Contains("прочитай")) result.Add("input");
            if (text.Contains("int ") || text.Contains("переменн") || text.Contains("тип")) result.Add("variables");
            if (text.Contains("+") || text.Contains("-") || text.Contains("*") || text.Contains("/") || text.Contains("арифмет")) result.Add("arithmetic");
            if (text.Contains("сравн") || text.Contains("больше") || text.Contains("меньше") || text.Contains("равн") || text.Contains(">") || text.Contains("<") || text.Contains("==") || text.Contains("!=")) result.Add("comparison");
            if (Regex.IsMatch(text, @"(^|[^a-zа-я0-9_])if([^a-zа-я0-9_]|$)", RegexOptions.IgnoreCase) || text.Contains("если") || text.Contains("иначе") || text.Contains("ветв") || text.Contains("условный") || text.Contains("условного") || titleTags.Contains("услов")) result.Add("if");
            if (text.Contains("for") || text.Contains("while") || text.Contains("цикл")) result.Add("loops");
            if (text.Contains("массив") || text.Contains("array") || text.Contains("vector")) result.Add("arrays");
            if (text.Contains("строк") || text.Contains("string") || text.Contains("char")) result.Add("strings");
            return result.Distinct().ToList();
        }

        private static string Preview(string? value, int max)
        {
            var text = Regex.Replace(value ?? string.Empty, @"\s+", " ").Trim();
            return text.Length <= max ? text : text[..max].TrimEnd() + "…";
        }

        private static int ScoreCourseForAgentContext(string normalizedMessage, string? title, string? description)
        {
            var text = NormalizeCourseSearchText(normalizedMessage);
            var normalizedTitle = NormalizeCourseSearchText(title);
            var normalizedDescription = NormalizeCourseSearchText(description);
            if (string.IsNullOrWhiteSpace(text)) return 0;

            var score = 0;
            if (!string.IsNullOrWhiteSpace(normalizedTitle) && text.Contains(normalizedTitle)) score += 90;

            foreach (var word in normalizedTitle.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(x => x.Length >= 2).Distinct())
            {
                if (text.Contains(word)) score += word is "c++" or "cpp" or "c#" or "python" or "pascal" ? 30 : 12;
            }

            foreach (var word in normalizedDescription.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(x => x.Length >= 4).Distinct().Take(30))
            {
                if (text.Contains(word)) score += 3;
            }

            if ((normalizedTitle.Contains("c++") || normalizedTitle.Contains("cpp")) && (text.Contains("c++") || text.Contains("cpp"))) score += 60;
            if (normalizedTitle.Contains("c#") && text.Contains("c#")) score += 60;
            if (normalizedTitle.Contains("python") && text.Contains("python")) score += 60;
            if (normalizedTitle.Contains("pascal") && text.Contains("pascal")) score += 60;
            if (normalizedTitle.Contains("основ") && text.Contains("основ")) score += 18;
            if (normalizedTitle.Contains("продвин") && text.Contains("продвин")) score += 18;
            return score;
        }

        private static string NormalizeCourseSearchText(string? value)
        {
            var text = (value ?? string.Empty).ToLowerInvariant();
            text = text.Replace("си++", "c++");
            text = text.Replace("с++", "c++");
            text = text.Replace("с #", "c#");
            text = text.Replace("си#", "c#");
            text = text.Replace("с#", "c#");
            text = text.Replace("c ++", "c++");
            text = text.Replace("c plus plus", "c++");
            text = text.Replace("cpp", "c++");
            text = text.Replace("си#", "c#");
            text = text.Replace("c sharp", "c#");
            text = text.Replace("csharp", "c#");
            text = text.Replace("питон", "python");
            return string.Join(' ', text.Split(new[] { '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries));
        }

        private static string Sha256Text(string value)
            => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value ?? string.Empty))).ToLowerInvariant();

        private bool IsAuthorized()
        {
            var expected = _config["TASKFORGE_AGENT_INTERNAL_KEY"]
                           ?? _config["Agent:InternalKey"]
                           ?? _config["API_INTERNAL_KEY"];
            var header = Request.Headers["X-Internal-Key"].ToString();
            if (string.IsNullOrWhiteSpace(expected) || string.IsNullOrWhiteSpace(header)) return false;

            var expectedBytes = Encoding.UTF8.GetBytes(expected);
            var headerBytes = Encoding.UTF8.GetBytes(header);
            return expectedBytes.Length == headerBytes.Length
                   && CryptographicOperations.FixedTimeEquals(expectedBytes, headerBytes);
        }


        private IActionResult? ValidateTerminalWorker(AgentRun run, string? workerId, DateTime now, string action)
        {
            var cleanWorkerId = CleanWorkerId(workerId);
            if (!string.Equals(run.WorkerId, cleanWorkerId, StringComparison.Ordinal))
            {
                return Conflict(new
                {
                    ok = false,
                    message = "This worker does not own the AI run lease.",
                    action,
                    runId = run.Id,
                    expectedWorkerId = run.WorkerId,
                    actualWorkerId = cleanWorkerId
                });
            }

            if (!string.Equals(run.Status, "running", StringComparison.OrdinalIgnoreCase))
            {
                return Conflict(new { ok = false, message = "AI run is not running.", action, runId = run.Id, status = run.Status });
            }

            // Completing/failing a run is the terminal write from the worker that currently
            // owns the run. Do not reject it only because the soft lease timestamp is missing
            // or slightly stale: heartbeats are best-effort, and rejecting the terminal write
            // turns an already-generated answer into a visible 409 failure. If another worker
            // has reclaimed the run, WorkerId changes and the ownership check above still
            // protects against stale writes.
            return null;
        }

        private IActionResult? ValidateActiveWorker(AgentRun run, string? workerId, DateTime now)
        {
            var cleanWorkerId = CleanWorkerId(workerId);
            if (!string.Equals(run.WorkerId, cleanWorkerId, StringComparison.Ordinal))
            {
                return Conflict(new
                {
                    ok = false,
                    message = "This worker does not own the AI run lease.",
                    runId = run.Id,
                    expectedWorkerId = run.WorkerId,
                    actualWorkerId = cleanWorkerId
                });
            }

            if (!string.Equals(run.Status, "running", StringComparison.OrdinalIgnoreCase))
            {
                return Conflict(new { ok = false, message = "AI run is not running.", runId = run.Id, status = run.Status });
            }

            if (!run.LeaseExpiresAtUtc.HasValue || run.LeaseExpiresAtUtc.Value < now)
            {
                return Conflict(new { ok = false, message = "AI run lease expired.", runId = run.Id, leaseExpiresAtUtc = run.LeaseExpiresAtUtc });
            }

            return null;
        }

        private async Task<int> NextStepSeqAsync(Guid runId)
        {
            return (await _db.AgentSteps.Where(x => x.RunId == runId).Select(x => (int?)x.Seq).MaxAsync() ?? 0) + 1;
        }

        private async Task SaveChangesWithAgentStepSeqRetryAsync(Guid runId)
        {
            for (var attempt = 0; ; attempt++)
            {
                NormalizePendingAgentSteps(runId);
                try
                {
                    await _db.SaveChangesAsync();
                    return;
                }
                catch (DbUpdateException ex) when (attempt < 3 && IsAgentStepSeqConflict(ex))
                {
                    await ReassignPendingAgentStepSeqsAsync(runId);
                }
            }
        }

        private void NormalizePendingAgentSteps(Guid runId)
        {
            var entries = _db.ChangeTracker.Entries<AgentStep>()
                .Where(x => (x.State == EntityState.Added || x.State == EntityState.Modified) && x.Entity.RunId == runId)
                .ToList();

            foreach (var entry in entries)
            {
                var step = entry.Entity;
                step.Kind = LimitDbText(step.Kind, AgentStepKindMaxLength) ?? "worker";
                step.Status = LimitDbText(step.Status, AgentStepStatusMaxLength) ?? "completed";
                step.ActionName = LimitDbText(step.ActionName, AgentStepActionNameMaxLength);
                step.Title = LimitDbText(step.Title, AgentStepTitleMaxLength) ?? "AI step";
                step.Summary = LimitDbText(step.Summary, AgentStepSummaryMaxLength);
            }
        }

        private static string? LimitDbText(string? value, int maxLength)
        {
            if (string.IsNullOrEmpty(value) || maxLength <= 0) return value;
            if (value.Length <= maxLength) return value;

            const string suffix = "... [truncated]";
            var take = Math.Max(0, maxLength - suffix.Length);
            return value.Substring(0, take).TrimEnd() + suffix;
        }

        private async Task ReassignPendingAgentStepSeqsAsync(Guid runId)
        {
            var pendingSteps = _db.ChangeTracker.Entries<AgentStep>()
                .Where(x => x.State == EntityState.Added && x.Entity.RunId == runId)
                .OrderBy(x => x.Entity.Seq)
                .ThenBy(x => x.Entity.CreatedAtUtc)
                .ToList();

            if (pendingSteps.Count == 0) return;

            var nextSeq = await NextStepSeqAsync(runId);
            foreach (var entry in pendingSteps)
                entry.Entity.Seq = nextSeq++;
        }

        private static bool IsAgentStepSeqConflict(DbUpdateException ex)
        {
            var text = ex.ToString();
            return text.Contains("IX_AgentSteps_RunId_Seq", StringComparison.OrdinalIgnoreCase)
                   || (text.Contains("AgentSteps", StringComparison.OrdinalIgnoreCase)
                       && text.Contains("Seq", StringComparison.OrdinalIgnoreCase)
                       && text.Contains("duplicate", StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsTerminalRunStatus(string? status)
        {
            var value = status ?? string.Empty;
            return value.StartsWith("completed", StringComparison.OrdinalIgnoreCase)
                   || value.Equals("failed", StringComparison.OrdinalIgnoreCase)
                   || value.Equals("canceled", StringComparison.OrdinalIgnoreCase);
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

        private static string MergeAgentMemoryPatch(string? existingJson, string patchRaw)
        {
            try
            {
                var existing = ParseObjectElements(existingJson);
                var patch = ParseObjectElements(patchRaw);
                foreach (var item in patch)
                {
                    if (item.Value.ValueKind == JsonValueKind.Null || item.Value.ValueKind == JsonValueKind.Undefined)
                        existing.Remove(item.Key);
                    else
                        existing[item.Key] = item.Value.Clone();
                }
                return JsonSerializer.Serialize(existing);
            }
            catch
            {
                return string.IsNullOrWhiteSpace(patchRaw) ? (existingJson ?? "{}") : patchRaw;
            }
        }

        private static Dictionary<string, JsonElement> ParseObjectElements(string? json)
        {
            var result = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(json)) return result;
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Object) return result;
                foreach (var prop in doc.RootElement.EnumerateObject())
                    result[prop.Name] = prop.Value.Clone();
            }
            catch
            {
            }
            return result;
        }

        private static Guid? ExtractActiveCourseIdFromMemory(string? memoryJson)
        {
            var ids = ExtractCourseIdsFromAgentMemory(memoryJson);
            return ids.Count == 1 ? ids[0] : null;
        }

        private static Guid? ExtractFirstCreatedDraftCourseId(IEnumerable<object> createdDrafts)
        {
            foreach (var item in createdDrafts)
            {
                if (item == null) continue;
                var prop = item.GetType().GetProperty("courseId") ?? item.GetType().GetProperty("CourseId");
                var value = prop?.GetValue(item);
                if (value is Guid id) return id;
                if (Guid.TryParse(value?.ToString(), out var parsed)) return parsed;
            }
            return null;
        }

        private static List<Guid> ExtractCourseIdsFromAgentMemory(string? memoryJson)
        {
            var result = new List<Guid>();
            if (string.IsNullOrWhiteSpace(memoryJson)) return result;
            try
            {
                using var doc = JsonDocument.Parse(memoryJson);
                if (doc.RootElement.ValueKind != JsonValueKind.Object) return result;
                AddCourseIdsFromElement(doc.RootElement, result);
            }
            catch
            {
            }
            return result.Distinct().Take(8).ToList();
        }

        private static void AddCourseIdsFromElement(JsonElement element, List<Guid> result)
        {
            if (result.Count >= 16) return;
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in element.EnumerateObject())
                {
                    if (prop.Name.Equals("courseId", StringComparison.OrdinalIgnoreCase)
                        || prop.Name.Equals("selectedCourseId", StringComparison.OrdinalIgnoreCase)
                        || prop.Name.Equals("activeCourseId", StringComparison.OrdinalIgnoreCase))
                    {
                        if (prop.Value.ValueKind == JsonValueKind.String && Guid.TryParse(prop.Value.GetString(), out var id))
                            result.Add(id);
                    }

                    if (prop.Name.Equals("selectedCourses", StringComparison.OrdinalIgnoreCase)
                        || prop.Name.Equals("usedCourses", StringComparison.OrdinalIgnoreCase)
                        || prop.Name.Equals("findings", StringComparison.OrdinalIgnoreCase)
                        || prop.Name.Equals("currentDraftBlueprint", StringComparison.OrdinalIgnoreCase)
                        || prop.Name.Equals("lastCourseAnalysis", StringComparison.OrdinalIgnoreCase)
                        || prop.Name.Equals("lastGapAudit", StringComparison.OrdinalIgnoreCase))
                    {
                        AddCourseIdsFromElement(prop.Value, result);
                    }
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray())
                    AddCourseIdsFromElement(item, result);
            }
        }

        private static string? ExtractRequestRawText(string? requestJson)
        {
            var doc = ParseJson(requestJson ?? "{}");
            if (doc is JsonElement el)
                return GetString(el, "rawText", "raw_text", "text");
            return null;
        }


        private static bool CanCreateHiddenDraftFromRun(AgentRun run, JsonElement requestJson)
        {
            var action = GetString(requestJson, "action", "scenarioId", "scenario_id");
            var createHiddenDraft = GetBool(requestJson, "createHiddenDraft", "create_hidden_draft") == true;
            return createHiddenDraft
                   && (string.Equals(run.ScenarioId, "polish_assignment_draft", StringComparison.OrdinalIgnoreCase)
                       || string.Equals(action, "polish_assignment_draft", StringComparison.OrdinalIgnoreCase));
        }

        private async Task<bool> CanRunWriteCourseAsync(AgentRun run, Guid courseId)
        {
            var actorId = run.ActingOnBehalfOfUserId ?? run.RequestedByUserId;
            var user = await _db.Users.AsNoTracking()
                .Where(x => x.Id == actorId)
                .Select(x => new { x.Id, x.Role })
                .FirstOrDefaultAsync();

            return user != null && await _courseAccess.CanEditCourseAsync(user.Id, user.Role, courseId);
        }

        private async Task<bool> DraftPlacementBelongsToCourseAsync(Guid courseId, Guid? beforeId, Guid? afterId)
        {
            var ids = new[] { beforeId, afterId }.Where(x => x.HasValue).Select(x => x!.Value).Distinct().ToList();
            if (ids.Count == 0) return true;

            var count = await _db.TaskAssignments.AsNoTracking()
                .CountAsync(x => ids.Contains(x.Id) && x.CourseId == courseId);
            return count == ids.Count;
        }

        private static bool DraftPassesBackendValidationGate(JsonElement data, string assignmentType, IReadOnlyList<DraftTestCase> tests)
        {
            if (!string.Equals(assignmentType, "code-test", StringComparison.OrdinalIgnoreCase))
                return true;

            var publicCount = tests.Count(x => !x.IsHidden && !string.IsNullOrWhiteSpace(x.ExpectedOutput));
            var hiddenCount = tests.Count(x => x.IsHidden && !string.IsNullOrWhiteSpace(x.ExpectedOutput));
            if (publicCount < 2 || hiddenCount < 2) return false;

            var normalizedPublic = tests.Where(x => !x.IsHidden).Select(NormalizeTestFingerprint).ToHashSet(StringComparer.Ordinal);
            var normalizedHidden = tests.Where(x => x.IsHidden).Select(NormalizeTestFingerprint).ToHashSet(StringComparer.Ordinal);
            if (normalizedPublic.Count == 0 || normalizedHidden.Count == 0) return false;
            if (normalizedHidden.All(normalizedPublic.Contains)) return false;

            if (TryGetObject(data, out var validation, "validation", "draftValidation", "draft_validation"))
            {
                if (GetBool(validation, "ok") == false) return false;
                if (TryGetObject(validation, out var testsNode, "tests", "testRun", "test_run"))
                    return GetBool(testsNode, "ok") == true;
            }

            if (TryGetObject(data, out var directTestRun, "testRun", "runnerValidation", "runner_validation"))
                return GetBool(directTestRun, "ok", "passed") == true;

            return false;
        }

        private static string NormalizeTestFingerprint(DraftTestCase test)
        {
            return $"{(test.Input ?? string.Empty).Replace("\r\n", "\n").Trim()}=>{(test.ExpectedOutput ?? string.Empty).Replace("\r\n", "\n").Trim()}";
        }

        private static bool TryGetObject(JsonElement element, out JsonElement obj, params string[] names)
        {
            obj = default;
            if (element.ValueKind != JsonValueKind.Object) return false;
            foreach (var name in names)
            {
                if (element.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.Object)
                {
                    obj = prop;
                    return true;
                }
            }
            return false;
        }

        private static bool IsHiddenDraftArtifactType(string? type)
            => string.Equals(type, "polished_assignment_draft", StringComparison.OrdinalIgnoreCase)
               || string.Equals(type, "assignment_draft_ready", StringComparison.OrdinalIgnoreCase);

        private async Task<object?> TryCreateHiddenDraftAssignmentAsync(AgentRun run, AgentRunArtifact artifact, DateTime now)
        {
            if (!IsHiddenDraftArtifactType(artifact.Type))
                return null;

            var parsedData = ParseJson(artifact.DataJson);
            var data = parsedData is JsonElement parsedElement ? parsedElement : default;
            if (data.ValueKind != JsonValueKind.Object) return null;

            var requestJsonObj = ParseJson(run.RequestJson ?? "{}");
            var requestJson = requestJsonObj is JsonElement requestEl ? requestEl : default;
            if (!CanCreateHiddenDraftFromRun(run, requestJson)) return null;

            var title = GetString(data, "title") ?? GetString(data, "assignmentTitle") ?? artifact.Title;
            var description = GetString(data, "description") ?? GetString(data, "condition") ?? GetString(data, "body");
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(description))
                return null;

            var assignmentType = NormalizeDraftAssignmentType(GetString(data, "assignmentType", "assignment_type", "taskType", "task_type"));
            if (assignmentType == "image-test")
                return null;

            var language = NormalizeRunnerLanguage(GetString(data, "language") ?? "cpp");
            var solution = GetReferenceSolutionForLanguage(data, language);

            var tests = assignmentType == "code-test" ? ExtractTestCases(data).ToList() : new List<DraftTestCase>();
            var testQuestions = assignmentType == "test" ? ExtractDraftTestQuestionSpecs(data).ToList() : new List<DraftTestQuestionSpec>();
            var mathBlocks = assignmentType == "math" ? ExtractDraftMathBlockSpecs(data).ToList() : new List<DraftMathBlockSpec>();

            if (assignmentType == "code-test" && (string.IsNullOrWhiteSpace(solution) || tests.Count == 0))
                return null;
            if (!DraftPassesBackendValidationGate(data, assignmentType, tests))
                return null;
            if (assignmentType == "test" && testQuestions.Count == 0)
                return null;
            if (assignmentType == "math" && mathBlocks.Count == 0)
                return null;

            var courseId = GetGuid(requestJson, "courseId")
                           ?? run.Conversation.CourseId
                           ?? GetGuid(data, "selectedCourseId", "courseId");
            var beforeId = GetGuid(data, "beforeAssignmentId") ?? GetGuid(requestJson, "beforeAssignmentId");
            var afterId = GetGuid(data, "afterAssignmentId") ?? GetGuid(requestJson, "afterAssignmentId");

            if (!courseId.HasValue && beforeId.HasValue)
            {
                courseId = await _db.TaskAssignments.AsNoTracking()
                    .Where(x => x.Id == beforeId.Value)
                    .Select(x => (Guid?)x.CourseId)
                    .FirstOrDefaultAsync();
            }
            if (!courseId.HasValue && afterId.HasValue)
            {
                courseId = await _db.TaskAssignments.AsNoTracking()
                    .Where(x => x.Id == afterId.Value)
                    .Select(x => (Guid?)x.CourseId)
                    .FirstOrDefaultAsync();
            }
            if (!courseId.HasValue) return null;
            if (!await CanRunWriteCourseAsync(run, courseId.Value)) return null;
            if (!await DraftPlacementBelongsToCourseAsync(courseId.Value, beforeId, afterId)) return null;

            var sourceTaskIndex = GetInt(data, "sourceTaskIndex", "source_task_index", "index");
            if (sourceTaskIndex.HasValue)
            {
                var existingDraft = await _db.TaskAssignments.AsNoTracking()
                    .Where(x => x.SourceAgentRunId == run.Id && x.SourceAgentTaskIndex == sourceTaskIndex.Value)
                    .Select(x => new
                    {
                        id = x.Id,
                        courseId = x.CourseId,
                        title = x.Title,
                        type = x.Type,
                        isHidden = x.IsHidden,
                        lifecycleStatus = x.LifecycleStatus,
                        sourceAgentRunId = x.SourceAgentRunId,
                        sourceAgentArtifactId = x.SourceAgentArtifactId,
                        sourceAgentTaskIndex = x.SourceAgentTaskIndex
                    })
                    .FirstOrDefaultAsync();
                if (existingDraft != null) return existingDraft;
            }

            var difficulty = Math.Clamp(GetInt(data, "difficulty") ?? 1, 1, 3);
            var rating = Math.Max(1, GetInt(data, "rating", "points", "score", "weight") ?? (difficulty * 10));

            var assignment = new TaskAssignment
            {
                Id = Guid.NewGuid(),
                CourseId = courseId.Value,
                Title = Trim(title, 200),
                Description = ToTiptapDocumentJson(description),
                Type = assignmentType,
                Difficulty = difficulty,
                Rating = rating,
                Sort = 0,
                AllowedLanguagesCsv = assignmentType == "code-test" ? language : (GetString(data, "allowedLanguagesCsv") ?? language),
                Tags = MergeTags(GetString(data, "tags"), "AI,черновик"),
                IsHidden = true,
                LifecycleStatus = "ready",
                IsAiDraft = true,
                SourceAgentRunId = run.Id,
                SourceAgentArtifactId = artifact.Id,
                SourceAgentTaskIndex = sourceTaskIndex,
                AiDraftJson = artifact.DataJson,
                CreatedAt = now,
                UpdatedAt = now,
                PolishedAtUtc = now,
                PublishedAtUtc = null,
            };

            foreach (var tc in tests)
            {
                assignment.TestCases.Add(new TaskTestCase
                {
                    Id = Guid.NewGuid(),
                    Input = tc.Input,
                    ExpectedOutput = tc.ExpectedOutput,
                    IsHidden = tc.IsHidden,
                });
            }

            await ApplyDraftPlacementAsync(assignment, beforeId, afterId, now);
            _db.TaskAssignments.Add(assignment);

            if (assignmentType == "test")
                AddDraftTestContent(assignment.Id, data, testQuestions, now);
            else if (assignmentType == "math")
                AddDraftMathContent(assignment.Id, data, mathBlocks, now);

            return new
            {
                id = assignment.Id,
                courseId = assignment.CourseId,
                title = assignment.Title,
                type = assignment.Type,
                isHidden = assignment.IsHidden,
                lifecycleStatus = assignment.LifecycleStatus,
                testCount = tests.Count,
                questionCount = testQuestions.Count,
                blockCount = mathBlocks.Count,
                sourceAgentRunId = run.Id,
                sourceAgentArtifactId = artifact.Id,
                sourceAgentTaskIndex = assignment.SourceAgentTaskIndex
            };
        }

        private static bool HasAnyProperty(JsonElement element, params string[] names)
        {
            if (element.ValueKind != JsonValueKind.Object) return false;
            foreach (var name in names)
            {
                if (element.TryGetProperty(name, out _)) return true;
            }
            return false;
        }

        private static List<Guid> ExtractGuidArray(JsonElement element, params string[] names)
        {
            var result = new List<Guid>();
            var arr = GetArrayElement(element, names);
            if (arr == null) return result;
            foreach (var item in arr.Value.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && Guid.TryParse(item.GetString(), out var id)) result.Add(id);
                else if (item.ValueKind == JsonValueKind.Object)
                {
                    var objId = GetGuid(item, "id", "assignmentId");
                    if (objId.HasValue) result.Add(objId.Value);
                }
            }
            return result.Distinct().ToList();
        }

        private static void ApplyCourseOrder(List<TaskAssignment> ordered, List<Guid> preferredOrder, DateTime now)
        {
            var byId = ordered.ToDictionary(x => x.Id);
            var next = new List<TaskAssignment>();
            foreach (var id in preferredOrder)
            {
                if (byId.TryGetValue(id, out var assignment) && !next.Any(x => x.Id == id))
                    next.Add(assignment);
            }
            foreach (var assignment in ordered.OrderBy(x => x.Sort).ThenBy(x => x.CreatedAt))
            {
                if (!next.Any(x => x.Id == assignment.Id)) next.Add(assignment);
            }
            for (var i = 0; i < next.Count; i++)
            {
                next[i].Sort = i;
                next[i].UpdatedAt = now;
            }
        }

        private static void ApplyCourseSortOverrides(List<TaskAssignment> ordered, Dictionary<Guid, int> sortOverrides, DateTime now)
        {
            var next = ordered
                .OrderBy(x => sortOverrides.TryGetValue(x.Id, out var value) ? value : x.Sort)
                .ThenBy(x => x.CreatedAt)
                .ToList();
            for (var i = 0; i < next.Count; i++)
            {
                next[i].Sort = i;
                next[i].UpdatedAt = now;
            }
        }

        private static int NormalizeCourseRatingsIfRequested(JsonElement data, List<TaskAssignment> assignments, DateTime now)
        {
            var policy = (GetString(data, "ratingPolicy", "difficultyRatingPolicy") ?? string.Empty).ToLowerInvariant();
            var shouldNormalize = GetBool(data, "normalizeRatings", "normalizeDifficultyRatings") == true
                                  || policy.Contains("difficulty")
                                  || policy.Contains("ровн")
                                  || policy.Contains("difficulty_based");
            if (!shouldNormalize) return 0;

            var changed = 0;
            foreach (var assignment in assignments)
            {
                var target = assignment.Difficulty switch
                {
                    <= 1 => 10,
                    2 => 20,
                    _ => 30,
                };
                if (assignment.Rating == target) continue;
                assignment.Rating = target;
                assignment.UpdatedAt = now;
                changed++;
            }
            return changed;
        }

        private static string NormalizeDraftAssignmentType(string? value)
        {
            var text = (value ?? string.Empty).Trim().ToLowerInvariant().Replace("_", "-").Replace(" ", "-");
            return text switch
            {
                "quiz" or "question" or "questions" or "test-task" or "task-test" or "text" => "test",
                "math-test" or "maths" or "formula" or "numeric" => "math",
                "code" or "programming" or "code-test" or "coding" => "code-test",
                "image" or "image-test" or "picture" => "image-test",
                "test" => "test",
                "math" => "math",
                _ => "code-test",
            };
        }

        private sealed record DraftOptionSpec(string Key, string Text);
        private sealed record DraftTestQuestionSpec(int Order, string Type, string Prompt, List<DraftOptionSpec> Options, List<string> CorrectOptionKeys, List<string> AcceptedAnswers, bool CaseSensitive, bool TrimAnswers);
        private sealed record DraftMathMatchPairSpec(string LeftKey, string RightKey);
        private sealed record DraftMathBlockSpec(int Order, string Kind, string Prompt, string? PromptContentJson, int Score, bool IsRequired, List<DraftOptionSpec> Options, List<string> CorrectOptionKeys, List<string> AcceptedAnswers, bool CaseSensitive, bool TrimAnswers, double? NumericTolerance, List<string> OrderItems, List<DraftOptionSpec> MatchLeftItems, List<DraftOptionSpec> MatchRightItems, List<DraftMathMatchPairSpec> MatchPairs);

        private static IEnumerable<DraftTestQuestionSpec> ExtractDraftTestQuestionSpecs(JsonElement data)
        {
            var spec = GetObjectElement(data, "testSpec", "test", "taskTest");
            var questions = GetArrayElement(spec ?? data, "questions", "items");
            if (questions == null) yield break;

            var order = 0;
            foreach (var item in questions.Value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var type = NormalizeDraftQuestionType(GetString(item, "type", "kind"));
                var prompt = GetString(item, "prompt", "question", "text", "title")?.Trim();
                if (string.IsNullOrWhiteSpace(prompt)) continue;

                var options = ExtractOptions(item).ToList();
                var correct = NormalizeCorrectKeys(GetStringList(item, "correctOptionKeys", "correctKeys", "answers", "correctAnswers", "correct"), options);
                var accepted = GetStringList(item, "acceptedAnswers", "answers", "correctAnswers", "answer");
                var caseSensitive = GetBool(item, "caseSensitive") ?? false;
                var trimAnswers = GetBool(item, "trim") ?? true;

                if ((type == "single-choice" || type == "multi-choice") && (options.Count < 2 || correct.Count == 0)) continue;
                if ((type == "fill" || type == "text") && accepted.Count == 0) continue;

                yield return new DraftTestQuestionSpec(
                    GetInt(item, "order") ?? order,
                    type,
                    prompt,
                    options,
                    correct,
                    accepted,
                    caseSensitive,
                    trimAnswers);
                order++;
            }
        }

        private void AddDraftTestContent(Guid assignmentId, JsonElement data, List<DraftTestQuestionSpec> questions, DateTime now)
        {
            var spec = GetObjectElement(data, "testSpec", "test", "taskTest");
            var settings = GetObjectElement(spec ?? data, "settings");
            _db.TaskTestSettings.Add(new TaskTestSettings
            {
                Id = Guid.NewGuid(),
                TaskAssignmentId = assignmentId,
                MaxAttempts = Math.Max(1, GetInt(settings ?? default, "maxAttempts", "max_attempts") ?? 1),
                PassPercent = Math.Clamp(GetInt(settings ?? default, "passPercent", "pass_percent") ?? 60, 0, 100),
                ShuffleQuestions = GetBool(settings ?? default, "shuffleQuestions", "shuffle_questions") ?? true,
                ShuffleAnswers = GetBool(settings ?? default, "shuffleAnswers", "shuffle_answers") ?? true,
                AllowReview = GetBool(settings ?? default, "allowReview", "allow_review") ?? true,
                AttemptTimeLimitsJson = JsonSerializer.Serialize(GetNullableIntList(settings ?? default, "attemptTimeLimitsSeconds", "attemptTimeLimits", "timeLimits")),
                CreatedAt = now,
                UpdatedAt = now,
            });

            foreach (var question in questions.OrderBy(x => x.Order).Select((value, index) => (value, index)))
            {
                var q = question.value;
                var dataJson = q.Type is "single-choice" or "multi-choice"
                    ? JsonSerializer.Serialize(new
                    {
                        options = q.Options.Select(x => new { key = x.Key, text = x.Text }).ToList(),
                        correctOptionKeys = q.CorrectOptionKeys
                    })
                    : JsonSerializer.Serialize(new
                    {
                        acceptedAnswers = q.AcceptedAnswers,
                        caseSensitive = q.CaseSensitive,
                        trim = q.TrimAnswers
                    });

                _db.TaskTestQuestions.Add(new TaskTestQuestion
                {
                    Id = Guid.NewGuid(),
                    TaskAssignmentId = assignmentId,
                    Order = question.index,
                    Type = q.Type,
                    Prompt = Trim(q.Prompt, 4000),
                    DataJson = dataJson,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
            }
        }

        private static IEnumerable<DraftMathBlockSpec> ExtractDraftMathBlockSpecs(JsonElement data)
        {
            var spec = GetObjectElement(data, "mathSpec", "math", "taskMath");
            var blocks = GetArrayElement(spec ?? data, "blocks", "items", "questions");
            if (blocks == null) yield break;

            var order = 0;
            foreach (var item in blocks.Value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var kind = NormalizeDraftMathKind(GetString(item, "kind", "type"));
                var prompt = GetString(item, "prompt", "question", "text", "title")?.Trim();
                if (string.IsNullOrWhiteSpace(prompt)) continue;

                var options = ExtractOptions(item).ToList();
                var correct = NormalizeCorrectKeys(GetStringList(item, "correctOptionKeys", "correctKeys", "answers", "correctAnswers", "correct"), options);
                var accepted = GetStringList(item, "acceptedAnswers", "answers", "correctAnswers", "answer");
                var orderItems = GetStringList(item, "orderItems", "items", "correctOrder");
                var leftItems = ExtractNamedOptions(item, "matchLeftItems", "leftItems", "left").ToList();
                var rightItems = ExtractNamedOptions(item, "matchRightItems", "rightItems", "right").ToList();
                var pairs = ExtractMatchPairs(item).ToList();

                if ((kind == "single-choice" || kind == "multi-choice") && (options.Count < 2 || correct.Count == 0)) continue;
                if ((kind == "number" || kind == "expression" || kind == "set") && accepted.Count == 0) continue;
                if (kind == "order" && orderItems.Count < 2) continue;
                if (kind == "match" && (leftItems.Count == 0 || rightItems.Count == 0 || pairs.Count == 0)) continue;

                yield return new DraftMathBlockSpec(
                    GetInt(item, "order") ?? order,
                    kind,
                    prompt,
                    GetString(item, "promptContentJson", "prompt_content_json"),
                    Math.Max(0, GetInt(item, "score") ?? (kind == "info" ? 0 : 1)),
                    GetBool(item, "isRequired", "required") ?? kind != "info",
                    options,
                    correct,
                    accepted,
                    GetBool(item, "caseSensitive") ?? false,
                    GetBool(item, "trim") ?? true,
                    GetDouble(item, "numericTolerance", "tolerance"),
                    orderItems,
                    leftItems,
                    rightItems,
                    pairs);
                order++;
            }
        }

        private void AddDraftMathContent(Guid assignmentId, JsonElement data, List<DraftMathBlockSpec> blocks, DateTime now)
        {
            var spec = GetObjectElement(data, "mathSpec", "math", "taskMath");
            var settings = GetObjectElement(spec ?? data, "settings");
            _db.TaskMathSettings.Add(new TaskMathSettings
            {
                Id = Guid.NewGuid(),
                TaskAssignmentId = assignmentId,
                MaxAttempts = Math.Max(1, GetInt(settings ?? default, "maxAttempts", "max_attempts") ?? 1),
                PassPercent = Math.Clamp(GetInt(settings ?? default, "passPercent", "pass_percent") ?? 60, 0, 100),
                ShuffleBlocks = GetBool(settings ?? default, "shuffleBlocks", "shuffle_blocks") ?? false,
                AllowReview = GetBool(settings ?? default, "allowReview", "allow_review") ?? true,
                AttemptTimeLimitsJson = JsonSerializer.Serialize(GetNullableIntList(settings ?? default, "attemptTimeLimitsSeconds", "attemptTimeLimits", "timeLimits")),
                CreatedAt = now,
                UpdatedAt = now,
            });

            foreach (var block in blocks.OrderBy(x => x.Order).Select((value, index) => (value, index)))
            {
                var b = block.value;
                var dataJson = b.Kind switch
                {
                    "single-choice" or "multi-choice" => JsonSerializer.Serialize(new
                    {
                        options = b.Options.Select(x => new { key = x.Key, text = x.Text }).ToList(),
                        correctOptionKeys = b.CorrectOptionKeys
                    }),
                    "number" or "expression" or "set" => JsonSerializer.Serialize(new
                    {
                        acceptedAnswers = b.AcceptedAnswers,
                        caseSensitive = b.CaseSensitive,
                        trim = b.TrimAnswers,
                        numericTolerance = b.NumericTolerance
                    }),
                    "order" => JsonSerializer.Serialize(new { items = b.OrderItems }),
                    "match" => JsonSerializer.Serialize(new
                    {
                        leftItems = b.MatchLeftItems.Select(x => new { key = x.Key, text = x.Text }).ToList(),
                        rightItems = b.MatchRightItems.Select(x => new { key = x.Key, text = x.Text }).ToList(),
                        pairs = b.MatchPairs.Select(x => new { leftKey = x.LeftKey, rightKey = x.RightKey }).ToList()
                    }),
                    _ => "{}"
                };

                _db.TaskMathBlocks.Add(new TaskMathBlock
                {
                    Id = Guid.NewGuid(),
                    TaskAssignmentId = assignmentId,
                    Order = block.index,
                    Kind = b.Kind,
                    Prompt = Trim(b.Prompt, 4000),
                    PromptContentJson = string.IsNullOrWhiteSpace(b.PromptContentJson) ? null : b.PromptContentJson,
                    DataJson = dataJson,
                    Score = b.Score,
                    IsRequired = b.IsRequired,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
            }
        }

        private static string NormalizeDraftQuestionType(string? value)
        {
            var text = (value ?? string.Empty).Trim().ToLowerInvariant().Replace("_", "-").Replace(" ", "-");
            return text switch
            {
                "multi" or "multiple" or "multiple-choice" or "multi-choice" => "multi-choice",
                "fill-in" or "fill-blank" or "short-answer" or "answer" => "fill",
                "free-text" or "open" or "text-answer" => "text",
                _ => "single-choice",
            };
        }

        private static string NormalizeDraftMathKind(string? value)
        {
            var text = (value ?? string.Empty).Trim().ToLowerInvariant().Replace("_", "-").Replace(" ", "-");
            return text switch
            {
                "info" or "theory" or "content" => "info",
                "num" or "numeric" or "number" => "number",
                "expr" or "expression" or "formula" => "expression",
                "set" or "sets" => "set",
                "multi" or "multiple" or "multiple-choice" or "multi-choice" => "multi-choice",
                "single" or "choice" or "single-choice" => "single-choice",
                "ordering" or "sort" or "order" => "order",
                "matching" or "match" => "match",
                _ => "number",
            };
        }

        private static JsonElement? GetObjectElement(JsonElement element, params string[] names)
        {
            if (element.ValueKind != JsonValueKind.Object) return null;
            foreach (var name in names)
            {
                if (element.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.Object)
                    return prop;
            }
            return null;
        }

        private static JsonElement? GetArrayElement(JsonElement element, params string[] names)
        {
            if (element.ValueKind != JsonValueKind.Object) return null;
            foreach (var name in names)
            {
                if (element.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.Array)
                    return prop;
            }
            return null;
        }

        private static IEnumerable<DraftOptionSpec> ExtractOptions(JsonElement element)
        {
            return ExtractNamedOptions(element, "options", "answers", "choices");
        }

        private static IEnumerable<DraftOptionSpec> ExtractNamedOptions(JsonElement element, params string[] names)
        {
            var arr = GetArrayElement(element, names);
            if (arr == null) yield break;
            var index = 0;
            foreach (var option in arr.Value.EnumerateArray())
            {
                var key = KeyForIndex(index);
                var text = string.Empty;
                if (option.ValueKind == JsonValueKind.Object)
                {
                    key = GetString(option, "key", "id", "value") ?? key;
                    text = GetString(option, "text", "label", "title", "value") ?? key;
                }
                else if (option.ValueKind == JsonValueKind.String)
                {
                    text = option.GetString() ?? string.Empty;
                }
                else if (option.ValueKind == JsonValueKind.Number || option.ValueKind == JsonValueKind.True || option.ValueKind == JsonValueKind.False)
                {
                    text = option.ToString();
                }
                if (!string.IsNullOrWhiteSpace(text))
                    yield return new DraftOptionSpec(Trim(key, 80), Trim(text, 1000));
                index++;
            }
        }

        private static IEnumerable<DraftMathMatchPairSpec> ExtractMatchPairs(JsonElement element)
        {
            var arr = GetArrayElement(element, "matchPairs", "pairs");
            if (arr == null) yield break;
            foreach (var pair in arr.Value.EnumerateArray())
            {
                if (pair.ValueKind != JsonValueKind.Object) continue;
                var left = GetString(pair, "leftKey", "left", "sourceKey");
                var right = GetString(pair, "rightKey", "right", "targetKey");
                if (!string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right))
                    yield return new DraftMathMatchPairSpec(Trim(left, 80), Trim(right, 80));
            }
        }

        private static List<string> NormalizeCorrectKeys(List<string> raw, List<DraftOptionSpec> options)
        {
            var result = new List<string>();
            foreach (var value in raw)
            {
                var match = options.FirstOrDefault(x => string.Equals(x.Key, value, StringComparison.OrdinalIgnoreCase))
                            ?? options.FirstOrDefault(x => string.Equals(x.Text, value, StringComparison.OrdinalIgnoreCase));
                var key = match?.Key ?? value;
                if (!string.IsNullOrWhiteSpace(key) && !result.Contains(key, StringComparer.OrdinalIgnoreCase))
                    result.Add(Trim(key, 80));
            }
            return result;
        }

        private static List<string> GetStringList(JsonElement element, params string[] names)
        {
            var result = new List<string>();
            if (element.ValueKind != JsonValueKind.Object) return result;
            foreach (var name in names)
            {
                if (!element.TryGetProperty(name, out var prop)) continue;
                if (prop.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in prop.EnumerateArray())
                    {
                        var value = item.ValueKind == JsonValueKind.String ? item.GetString() : item.ToString();
                        if (!string.IsNullOrWhiteSpace(value)) result.Add(Trim(value, 1000));
                    }
                }
                else
                {
                    var value = prop.ValueKind == JsonValueKind.String ? prop.GetString() : prop.ToString();
                    foreach (var part in (value ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        if (!string.IsNullOrWhiteSpace(part)) result.Add(Trim(part, 1000));
                }
                if (result.Count > 0) break;
            }
            return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static List<int?> GetNullableIntList(JsonElement element, params string[] names)
        {
            var result = new List<int?>();
            if (element.ValueKind != JsonValueKind.Object) return result;
            var arr = GetArrayElement(element, names);
            if (arr == null) return result;
            foreach (var item in arr.Value.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Null) result.Add(null);
                else if (item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out var n)) result.Add(n);
                else if (item.ValueKind == JsonValueKind.String && int.TryParse(item.GetString(), out var parsed)) result.Add(parsed);
            }
            return result;
        }

        private static double? GetDouble(JsonElement element, params string[] names)
        {
            if (element.ValueKind != JsonValueKind.Object) return null;
            foreach (var name in names)
            {
                if (!element.TryGetProperty(name, out var prop)) continue;
                if (prop.ValueKind == JsonValueKind.Number && prop.TryGetDouble(out var d)) return d;
                if (prop.ValueKind == JsonValueKind.String && double.TryParse(prop.GetString(), out var parsed)) return parsed;
            }
            return null;
        }

        private static string KeyForIndex(int index)
        {
            const string letters = "abcdefghijklmnopqrstuvwxyz";
            if (index >= 0 && index < letters.Length) return letters[index].ToString();
            return $"v{index + 1}";
        }

        private async Task ApplyDraftPlacementAsync(TaskAssignment assignment, Guid? beforeId, Guid? afterId, DateTime now)
        {
            var ordered = await _db.TaskAssignments
                .Where(x => x.CourseId == assignment.CourseId)
                .OrderBy(x => x.Sort)
                .ThenBy(x => x.CreatedAt)
                .ToListAsync();

            ordered.RemoveAll(x => x.Id == assignment.Id);

            var insertIndex = ordered.Count;
            if (beforeId.HasValue)
            {
                var beforeIndex = ordered.FindIndex(x => x.Id == beforeId.Value);
                if (beforeIndex >= 0) insertIndex = beforeIndex;
            }
            else if (afterId.HasValue)
            {
                var afterIndex = ordered.FindIndex(x => x.Id == afterId.Value);
                if (afterIndex >= 0) insertIndex = afterIndex + 1;
            }

            ordered.Insert(Math.Clamp(insertIndex, 0, ordered.Count), assignment);

            if (assignment.SourceAgentTaskIndex.HasValue && (beforeId.HasValue || afterId.HasValue))
            {
                var start = afterId.HasValue ? ordered.FindIndex(x => x.Id == afterId.Value) + 1 : 0;
                if (start < 0) start = 0;

                var endExclusive = beforeId.HasValue ? ordered.FindIndex(x => x.Id == beforeId.Value) : ordered.Count;
                if (endExclusive < 0) endExclusive = ordered.Count;

                if (start < endExclusive)
                {
                    var segment = ordered
                        .Skip(start)
                        .Take(endExclusive - start)
                        .Select((item, originalIndex) => new { item, originalIndex })
                        .OrderBy(x => x.item.IsAiDraft && x.item.SourceAgentTaskIndex.HasValue ? 0 : 1)
                        .ThenBy(x => x.item.IsAiDraft && x.item.SourceAgentTaskIndex.HasValue ? x.item.SourceAgentTaskIndex.GetValueOrDefault() : int.MaxValue)
                        .ThenBy(x => x.originalIndex)
                        .Select(x => x.item)
                        .ToList();

                    for (var i = 0; i < segment.Count; i++)
                        ordered[start + i] = segment[i];
                }
            }

            for (var i = 0; i < ordered.Count; i++)
            {
                ordered[i].Sort = i;
                ordered[i].UpdatedAt = now;
            }
        }

        private async Task<int?> GetDraftInsertSortAsync(Guid courseId, Guid? beforeId, Guid? afterId)
        {
            if (beforeId.HasValue)
            {
                var beforeSort = await _db.TaskAssignments.AsNoTracking()
                    .Where(x => x.Id == beforeId.Value && x.CourseId == courseId)
                    .Select(x => (int?)x.Sort)
                    .FirstOrDefaultAsync();
                if (beforeSort.HasValue) return beforeSort.Value;
            }
            if (afterId.HasValue)
            {
                var afterSort = await _db.TaskAssignments.AsNoTracking()
                    .Where(x => x.Id == afterId.Value && x.CourseId == courseId)
                    .Select(x => (int?)x.Sort)
                    .FirstOrDefaultAsync();
                if (afterSort.HasValue) return afterSort.Value + 1;
            }
            return null;
        }

        private static string ToTiptapDocumentJson(string value)
        {
            var text = value ?? string.Empty;
            if (string.IsNullOrWhiteSpace(text)) return text;

            try
            {
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("type", out var type)
                    && string.Equals(type.GetString(), "doc", StringComparison.OrdinalIgnoreCase))
                {
                    return text;
                }
            }
            catch
            {
            }

            // HTML from the real TipTap editor should be preserved as-is.
            // Important: do not treat C++ snippets like <iostream> as HTML.
            if (Regex.IsMatch(text, @"<\s*(p|div|br|ul|ol|li|h[1-6]|blockquote|pre|code)\b", RegexOptions.IgnoreCase))
                return text;

            var content = ParseMarkdownishStatementToTiptapContent(text);
            return JsonSerializer.Serialize(new { type = "doc", content });
        }

        private static List<object> ParseMarkdownishStatementToTiptapContent(string value)
        {
            var lines = (value ?? string.Empty).Replace("\r", string.Empty).Split('\n');
            var content = new List<object>();
            var i = 0;

            while (i < lines.Length)
            {
                var line = lines[i] ?? string.Empty;
                if (string.IsNullOrWhiteSpace(line))
                {
                    i++;
                    continue;
                }

                var fence = Regex.Match(line, @"^\s*```\s*([\w+-]*)\s*$");
                if (fence.Success)
                {
                    var codeLines = new List<string>();
                    var language = string.IsNullOrWhiteSpace(fence.Groups[1].Value) ? null : fence.Groups[1].Value.Trim();
                    i++;
                    while (i < lines.Length && !Regex.IsMatch(lines[i] ?? string.Empty, @"^\s*```\s*$"))
                    {
                        codeLines.Add(lines[i] ?? string.Empty);
                        i++;
                    }
                    if (i < lines.Length) i++;

                    var codeText = string.Join("\n", codeLines);
                    var codeBlock = new Dictionary<string, object?>
                    {
                        ["type"] = "codeBlock",
                        ["attrs"] = new Dictionary<string, object?> { ["language"] = language },
                        ["content"] = string.IsNullOrEmpty(codeText)
                            ? Array.Empty<object>()
                            : new object[] { new Dictionary<string, object?> { ["type"] = "text", ["text"] = codeText } }
                    };
                    content.Add(codeBlock);
                    continue;
                }

                var heading = Regex.Match(line, @"^\s{0,3}(#{1,3})\s+(.+)$");
                if (heading.Success)
                {
                    content.Add(new Dictionary<string, object?>
                    {
                        ["type"] = "heading",
                        ["attrs"] = new Dictionary<string, object?> { ["level"] = heading.Groups[1].Value.Length },
                        ["content"] = InlineTextToTiptapNodes(heading.Groups[2].Value.Trim())
                    });
                    i++;
                    continue;
                }

                var ordered = Regex.Match(line, @"^\s*(\d+)[\.)]\s+(.+)$");
                if (ordered.Success)
                {
                    var start = int.TryParse(ordered.Groups[1].Value, out var parsedStart) ? Math.Max(1, parsedStart) : 1;
                    var items = new List<object>();
                    while (i < lines.Length)
                    {
                        var current = lines[i] ?? string.Empty;
                        if (string.IsNullOrWhiteSpace(current))
                        {
                            var next = i + 1 < lines.Length ? lines[i + 1] ?? string.Empty : string.Empty;
                            if (Regex.IsMatch(next, @"^\s*\d+[\.)]\s+"))
                            {
                                i++;
                                continue;
                            }
                            break;
                        }

                        var itemMatch = Regex.Match(current, @"^\s*\d+[\.)]\s+(.+)$");
                        if (!itemMatch.Success) break;
                        items.Add(TiptapListItem(itemMatch.Groups[1].Value.Trim()));
                        i++;
                    }
                    content.Add(new Dictionary<string, object?>
                    {
                        ["type"] = "orderedList",
                        ["attrs"] = new Dictionary<string, object?> { ["start"] = start },
                        ["content"] = items
                    });
                    continue;
                }

                var bullet = Regex.Match(line, @"^\s*[-*]\s+(.+)$");
                if (bullet.Success)
                {
                    var items = new List<object>();
                    while (i < lines.Length)
                    {
                        var current = lines[i] ?? string.Empty;
                        if (string.IsNullOrWhiteSpace(current))
                        {
                            var next = i + 1 < lines.Length ? lines[i + 1] ?? string.Empty : string.Empty;
                            if (Regex.IsMatch(next, @"^\s*[-*]\s+"))
                            {
                                i++;
                                continue;
                            }
                            break;
                        }

                        var itemMatch = Regex.Match(current, @"^\s*[-*]\s+(.+)$");
                        if (!itemMatch.Success) break;
                        items.Add(TiptapListItem(itemMatch.Groups[1].Value.Trim()));
                        i++;
                    }
                    content.Add(new Dictionary<string, object?>
                    {
                        ["type"] = "bulletList",
                        ["content"] = items
                    });
                    continue;
                }

                content.Add(TiptapParagraph(line.Trim()));
                i++;
            }

            return content;
        }

        private static Dictionary<string, object?> TiptapParagraph(string text)
        {
            return new Dictionary<string, object?>
            {
                ["type"] = "paragraph",
                ["content"] = InlineTextToTiptapNodes(text)
            };
        }

        private static Dictionary<string, object?> TiptapListItem(string text)
        {
            return new Dictionary<string, object?>
            {
                ["type"] = "listItem",
                ["content"] = new object[] { TiptapParagraph(text) }
            };
        }

        private static List<object> InlineTextToTiptapNodes(string value)
        {
            var text = value ?? string.Empty;
            var nodes = new List<object>();
            var i = 0;

            while (i < text.Length)
            {
                var start = text.IndexOf('`', i);
                if (start < 0)
                {
                    AddTiptapTextNode(nodes, text[i..], null);
                    break;
                }

                if (start > i)
                    AddTiptapTextNode(nodes, text[i..start], null);

                var end = text.IndexOf('`', start + 1);
                if (end < 0)
                {
                    AddTiptapTextNode(nodes, text[start..], null);
                    break;
                }

                AddTiptapTextNode(nodes, text[(start + 1)..end], "code");
                i = end + 1;
            }

            return nodes;
        }

        private static void AddTiptapTextNode(List<object> nodes, string text, string? markType)
        {
            if (string.IsNullOrEmpty(text)) return;

            var node = new Dictionary<string, object?>
            {
                ["type"] = "text",
                ["text"] = text
            };

            if (!string.IsNullOrWhiteSpace(markType))
            {
                node["marks"] = new object[]
                {
                    new Dictionary<string, object?> { ["type"] = markType }
                };
            }

            nodes.Add(node);
        }

        private sealed record DraftTestCase(string Input, string ExpectedOutput, bool IsHidden);

        private static IEnumerable<DraftTestCase> ExtractTestCases(JsonElement data)
        {
            foreach (var group in new[] { (Name: "publicTests", Hidden: false), (Name: "hiddenTests", Hidden: true), (Name: "testCases", Hidden: false) })
            {
                if (!data.TryGetProperty(group.Name, out var arr) || arr.ValueKind != JsonValueKind.Array) continue;
                foreach (var item in arr.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    var input = GetString(item, "input") ?? string.Empty;
                    var output = GetString(item, "expectedOutput", "output") ?? string.Empty;
                    var hidden = GetBool(item, "isHidden", "hidden") ?? group.Hidden;
                    yield return new DraftTestCase(input, output, hidden);
                }
            }
        }

        private static string MergeTags(string? existing, string required)
        {
            var tags = new List<string>();
            foreach (var raw in new[] { existing, required })
            {
                foreach (var part in (raw ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (!tags.Contains(part, StringComparer.OrdinalIgnoreCase)) tags.Add(part);
                }
            }
            return string.Join(",", tags);
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


        private static List<AgentAttachmentDto> ExtractAgentAttachments(string? dataJson)
        {
            if (string.IsNullOrWhiteSpace(dataJson)) return new List<AgentAttachmentDto>();
            try
            {
                using var doc = JsonDocument.Parse(dataJson);
                return ExtractAgentAttachments(doc.RootElement);
            }
            catch
            {
                return new List<AgentAttachmentDto>();
            }
        }

        private static List<AgentAttachmentDto> ExtractAgentAttachments(JsonElement element)
        {
            if (element.ValueKind != JsonValueKind.Object) return new List<AgentAttachmentDto>();
            if (!element.TryGetProperty("attachments", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return new List<AgentAttachmentDto>();

            var result = new List<AgentAttachmentDto>();
            foreach (var item in arr.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var key = GetString(item, "key", "Key");
                if (string.IsNullOrWhiteSpace(key)) continue;
                result.Add(new AgentAttachmentDto
                {
                    Key = key.Trim(),
                    FileName = GetString(item, "fileName", "FileName", "name") ?? Path.GetFileName(key),
                    ContentType = GetString(item, "contentType", "ContentType") ?? "application/octet-stream",
                    SizeBytes = GetLong(item, "sizeBytes", "SizeBytes", "size") ?? 0,
                    Url = GetString(item, "url", "Url") ?? $"/api/private-files/{Uri.EscapeDataString(key)}",
                    ExtractedText = GetString(item, "extractedText", "ExtractedText"),
                });
            }
            return result;
        }

        private async Task<List<object>> BuildAgentFileContextsAsync(IEnumerable<AgentAttachmentDto> attachments)
        {
            var result = new List<object>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in attachments ?? Enumerable.Empty<AgentAttachmentDto>())
            {
                if (string.IsNullOrWhiteSpace(item.Key) || !seen.Add(item.Key)) continue;
                var contentType = string.IsNullOrWhiteSpace(item.ContentType) ? "application/octet-stream" : item.ContentType;
                var fileName = string.IsNullOrWhiteSpace(item.FileName) ? Path.GetFileName(item.Key) : item.FileName;
                var text = item.ExtractedText;
                var extractStatus = string.IsNullOrWhiteSpace(text) ? "not_extracted" : "provided_by_client";

                if (string.IsNullOrWhiteSpace(text) && IsTextLikeAgentAttachment(fileName, contentType, item.SizeBytes))
                {
                    try
                    {
                        var (stream, actualContentType) = await _store.GetAsync(item.Key);
                        await using (stream)
                        {
                            text = await ReadTextPreviewAsync(stream, 160_000);
                        }
                        contentType = string.IsNullOrWhiteSpace(actualContentType) ? contentType : actualContentType;
                        extractStatus = string.IsNullOrWhiteSpace(text) ? "empty" : "extracted_text_preview";
                    }
                    catch (Exception ex)
                    {
                        extractStatus = "extract_failed: " + ex.GetType().Name;
                    }
                }
                else if (string.IsNullOrWhiteSpace(text))
                {
                    extractStatus = "binary_or_unsupported";
                }

                result.Add(new
                {
                    key = item.Key,
                    fileName,
                    contentType,
                    sizeBytes = item.SizeBytes,
                    url = item.Url,
                    extractStatus,
                    textPreview = Trim(text, 120_000)
                });
            }
            return result;
        }

        private static bool IsTextLikeAgentAttachment(string? fileName, string? contentType, long sizeBytes)
        {
            if (sizeBytes > 2_000_000) return false;
            var ct = (contentType ?? string.Empty).ToLowerInvariant();
            if (ct.StartsWith("text/") || ct.Contains("json") || ct.Contains("xml") || ct.Contains("csv") || ct.Contains("yaml") || ct.Contains("markdown")) return true;
            var ext = Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant();
            return ext is ".txt" or ".md" or ".markdown" or ".json" or ".csv" or ".tsv" or ".xml" or ".html" or ".htm" or ".yaml" or ".yml" or ".cs" or ".js" or ".jsx" or ".ts" or ".tsx" or ".py" or ".cpp" or ".hpp" or ".c" or ".h" or ".java" or ".pas" or ".sql";
        }

        private static async Task<string> ReadTextPreviewAsync(Stream stream, int maxBytes)
        {
            var buffer = new byte[Math.Max(1024, maxBytes)];
            var total = 0;
            while (total < maxBytes)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(total, maxBytes - total));
                if (read <= 0) break;
                total += read;
            }
            if (total <= 0) return string.Empty;
            var text = Encoding.UTF8.GetString(buffer, 0, total);
            text = text.Replace("\0", "").Trim();
            return total >= maxBytes ? text + "\n[... файл обрезан для AI-контекста ...]" : text;
        }

        private static long? GetLong(JsonElement element, params string[] names)
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


        private static Guid? GetGuid(JsonElement element, params string[] names)
        {
            if (element.ValueKind != JsonValueKind.Object) return null;
            foreach (var name in names)
            {
                if (!element.TryGetProperty(name, out var prop)) continue;
                if (prop.ValueKind == JsonValueKind.String && Guid.TryParse(prop.GetString(), out var id)) return id;
            }
            return null;
        }

        private static int? GetInt(JsonElement element, params string[] names)
        {
            if (element.ValueKind != JsonValueKind.Object) return null;
            foreach (var name in names)
            {
                if (!element.TryGetProperty(name, out var prop)) continue;
                if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var n)) return n;
                if (prop.ValueKind == JsonValueKind.String && int.TryParse(prop.GetString(), out var parsed)) return parsed;
            }
            return null;
        }

        private static bool? GetBool(JsonElement element, params string[] names)
        {
            if (element.ValueKind != JsonValueKind.Object) return null;
            foreach (var name in names)
            {
                if (!element.TryGetProperty(name, out var prop)) continue;
                if (prop.ValueKind == JsonValueKind.True) return true;
                if (prop.ValueKind == JsonValueKind.False) return false;
                if (prop.ValueKind == JsonValueKind.String && bool.TryParse(prop.GetString(), out var parsed)) return parsed;
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
            attachments = ExtractAgentAttachments(message.DataJson),
            clientMessageId = message.ClientMessageId,
            createdAtUtc = message.CreatedAtUtc,
        };

        private static string? GetReferenceSolutionForLanguage(JsonElement data, string? language)
        {
            var lang = NormalizeRunnerLanguage(language);
            return lang switch
            {
                "cpp" => GetString(data, "referenceSolutionCpp", "solutionCpp", "referenceSolutionCxx", "solutionCxx", "referenceSolution", "solution"),
                "csharp" => GetString(data, "referenceSolutionCsharp", "referenceSolutionCSharp", "referenceSolutionCs", "solutionCsharp", "solutionCSharp", "solutionCs", "referenceSolution", "solution"),
                "python" => GetString(data, "referenceSolutionPython", "solutionPython", "referenceSolutionPy", "solutionPy", "referenceSolution", "solution"),
                "javascript" => GetString(data, "referenceSolutionJavascript", "referenceSolutionJavaScript", "referenceSolutionJs", "solutionJavascript", "solutionJavaScript", "solutionJs", "referenceSolution", "solution"),
                "pascal" => GetString(data, "referenceSolutionPascal", "solutionPascal", "referenceSolutionPas", "solutionPas", "referenceSolution", "solution"),
                "java" => GetString(data, "referenceSolutionJava", "solutionJava", "referenceSolution", "solution"),
                _ => GetString(data, "referenceSolution", "solution", "referenceSolutionCpp", "solutionCpp", "referenceSolutionCsharp", "referenceSolutionCSharp", "referenceSolutionCs", "solutionCsharp", "solutionCSharp", "solutionCs", "referenceSolutionPython", "solutionPython", "referenceSolutionJavascript", "referenceSolutionJavaScript", "referenceSolutionJs", "solutionJavascript", "solutionJavaScript", "solutionJs", "referenceSolutionPascal", "solutionPascal", "referenceSolutionJava", "solutionJava")
            };
        }

        private static string NormalizeRunnerLanguage(string? value)
        {
            var text = (value ?? string.Empty).Trim().ToLowerInvariant();
            return text switch
            {
                "c++" or "cpp" or "g++" or "gcc" or "cxx" or "си++" or "с++" => "cpp",
                "c#" or "csharp" or "cs" or "sharp" or "си#" or "с#" or "шарп" => "csharp",
                "py" or "python3" or "python" or "питон" => "python",
                "js" or "javascript" or "java-script" or "node" or "nodejs" or "node.js" => "javascript",
                "pas" or "pascal" or "паскаль" => "pascal",
                "java" or "джава" => "java",
                _ => string.IsNullOrWhiteSpace(text) ? "cpp" : text
            };
        }

        private static string Trim(string value, int max)
        {
            value = value.Trim();
            return value.Length <= max ? value : value[..max];
        }
    }
}
