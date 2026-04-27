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
        private readonly ICompilerService _compiler;

        public InternalAgentController(ApplicationDbContext db, IConfiguration config, IHubContext<AgentHub> hub, ICompilerService compiler)
        {
            _db = db;
            _config = config;
            _hub = hub;
            _compiler = compiler;
        }

        [HttpPost("claim-next")]
        public async Task<IActionResult> ClaimNext([FromBody] InternalAgentClaimNextRequest request)
        {
            if (!IsAuthorized()) return Unauthorized();

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
            await SaveChangesWithAgentStepSeqRetryAsync(run.Id);

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
            if (run.FinishedAtUtc.HasValue || IsTerminalRunStatus(run.Status))
                return Ok(new { ok = true, runId = run.Id, alreadyCompleted = true, status = run.Status });

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

            var nextStepSeq = await NextStepSeqAsync(run.Id);

            var artifacts = ExtractArtifacts(run.Id, result, now).ToList();
            foreach (var artifact in artifacts)
                _db.AgentRunArtifacts.Add(artifact);

            var createdDrafts = new List<object>();
            foreach (var artifact in artifacts)
            {
                var created = await TryCreateHiddenDraftAssignmentAsync(run, artifact, now);
                if (created != null) createdDrafts.Add(created);
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

            _db.AgentSteps.Add(new AgentStep
            {
                Id = Guid.NewGuid(),
                RunId = run.Id,
                Seq = nextStepSeq++,
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

            var memoryPatchRaw = GetRaw(result, "memoryPatch", "memory_patch");
            if (!string.IsNullOrWhiteSpace(memoryPatchRaw))
                run.Conversation.MemoryJson = memoryPatchRaw;

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

            var run = await _db.AgentRuns.Include(x => x.Conversation).FirstOrDefaultAsync(x => x.Id == runId);
            if (run == null) return NotFound();
            if (run.FinishedAtUtc.HasValue || IsTerminalRunStatus(run.Status))
                return Ok(new { ok = true, runId = run.Id, alreadyCompleted = true, status = run.Status });

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

            await SaveChangesWithAgentStepSeqRetryAsync(run.Id);

            await BroadcastAsync(run.ConversationId, "message.created", new { message = ToMessagePayload(message) });
            await BroadcastAsync(run.ConversationId, "run.failed", new { runId = run.Id, status = run.Status, error = ParseJson(errorRaw) });
            return Ok(new { ok = true, runId = run.Id });
        }


        [HttpPost("tools/run-tests")]
        public async Task<IActionResult> RunTestsTool([FromBody] TestRunRequestDto request)
        {
            if (!IsAuthorized()) return Unauthorized();
            request.Language = NormalizeRunnerLanguage(request.Language);
            var results = await _compiler.RunTestsAsync(request);
            return Ok(new
            {
                ok = results.All(x => x.Passed && string.Equals(x.Status, "ok", StringComparison.OrdinalIgnoreCase)),
                results
            });
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
            var normalizedUserText = NormalizeCourseSearchText(lastUserText);
            var targetConcepts = InferTargetConcepts(normalizedUserText);

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
                type = "assistant_chat_turn",
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
                focusAssignments,
                targetAssignments = focusAssignments,
                courseOutline = selectedOutline,
                courseDigest,
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
                type = "assistant_chat_turn",
                jobType = "assistant_chat_turn",
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

        private async Task SaveChangesWithAgentStepSeqRetryAsync(Guid runId)
        {
            for (var attempt = 0; ; attempt++)
            {
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

        private static string? ExtractRequestRawText(string? requestJson)
        {
            var doc = ParseJson(requestJson ?? "{}");
            if (doc is JsonElement el)
                return GetString(el, "rawText", "raw_text", "text");
            return null;
        }


        private async Task<object?> TryCreateHiddenDraftAssignmentAsync(AgentRun run, AgentRunArtifact artifact, DateTime now)
        {
            if (!string.Equals(artifact.Type, "polished_assignment_draft", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(artifact.Type, "assignment_draft_ready", StringComparison.OrdinalIgnoreCase))
                return null;

            var parsedData = ParseJson(artifact.DataJson);
            var data = parsedData is JsonElement parsedElement ? parsedElement : default;
            if (data.ValueKind != JsonValueKind.Object) return null;

            var requestJsonObj = ParseJson(run.RequestJson ?? "{}");
            var requestJson = requestJsonObj is JsonElement requestEl ? requestEl : default;

            var title = GetString(data, "title") ?? GetString(data, "assignmentTitle") ?? artifact.Title;
            var description = GetString(data, "description") ?? GetString(data, "condition") ?? GetString(data, "body");
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(description))
                return null;

            var assignmentType = NormalizeDraftAssignmentType(GetString(data, "assignmentType", "assignment_type", "taskType", "task_type"));
            if (assignmentType == "image-test")
                return null;

            var language = NormalizeRunnerLanguage(GetString(data, "language") ?? "cpp");
            var solution = language == "python"
                ? GetString(data, "referenceSolutionPython", "referenceSolution", "solutionPython", "solution")
                : GetString(data, "referenceSolutionCpp", "referenceSolution", "solutionCpp", "solution");

            var tests = assignmentType == "code-test" ? ExtractTestCases(data).ToList() : new List<DraftTestCase>();
            var testQuestions = assignmentType == "test" ? ExtractDraftTestQuestionSpecs(data).ToList() : new List<DraftTestQuestionSpec>();
            var mathBlocks = assignmentType == "math" ? ExtractDraftMathBlockSpecs(data).ToList() : new List<DraftMathBlockSpec>();

            if (assignmentType == "code-test" && (string.IsNullOrWhiteSpace(solution) || tests.Count == 0))
                return null;
            if (assignmentType == "test" && testQuestions.Count == 0)
                return null;
            if (assignmentType == "math" && mathBlocks.Count == 0)
                return null;

            var courseId = GetGuid(data, "selectedCourseId", "courseId")
                           ?? run.Conversation.CourseId
                           ?? GetGuid(requestJson, "courseId");
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

            var assignment = new TaskAssignment
            {
                Id = Guid.NewGuid(),
                CourseId = courseId.Value,
                Title = Trim(title, 200),
                Description = ToTiptapDocumentJson(description),
                Type = assignmentType,
                Difficulty = Math.Clamp(GetInt(data, "difficulty") ?? 1, 1, 3),
                Rating = 1,
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
            return string.Join(',', tags);
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
            clientMessageId = message.ClientMessageId,
            createdAtUtc = message.CreatedAtUtc,
        };

        private static string NormalizeRunnerLanguage(string? value)
        {
            var text = (value ?? string.Empty).Trim().ToLowerInvariant();
            return text switch
            {
                "c++" or "cpp" or "g++" or "gcc" or "cxx" => "cpp",
                "c#" or "csharp" or "cs" => "csharp",
                "py" or "python3" or "python" => "python",
                "js" or "javascript" or "node" or "nodejs" => "javascript",
                "pas" or "pascal" => "pascal",
                "java" => "java",
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
