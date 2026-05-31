using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using taskforge.Data;
using taskforge.Data.Models.DTO;
using taskforge.Data.Models.Entities;
using taskforge.Services.Interfaces;

namespace taskforge.Services.Agent;

public sealed class AgentCourseEditApplyService
{
    private const int MaxArtifactDataJsonChars = 1_000_000;
    private const int MaxAssignmentUpdates = 100;
    private const int MaxOrderIds = 500;
    private const int MaxCodeTestCases = 50;
    private const int MaxTestInputChars = 16_000;
    private const int MaxTestOutputChars = 16_000;
    private const int MaxReferenceSolutionChars = 120_000;
    private const int MaxApplyNoteChars = 1_000;

    private readonly ApplicationDbContext _db;
    private readonly ICourseAccessService _courseAccess;
    private readonly ICompilerService _compiler;

    public AgentCourseEditApplyService(ApplicationDbContext db, ICourseAccessService courseAccess, ICompilerService compiler)
    {
        _db = db;
        _courseAccess = courseAccess;
        _compiler = compiler;
    }

    public async Task<AgentApplyArtifactResult> ApplyAsync(Guid artifactId, Guid? expectedRunId, Guid currentUserId, string? currentUserRole, AgentApplyArtifactRequest request, CancellationToken ct = default)
    {
        var dryRun = request.DryRun;
        var now = DateTime.UtcNow;
        var artifact = await _db.AgentRunArtifacts
            .Include(x => x.Run)
                .ThenInclude(x => x.Conversation)
            .FirstOrDefaultAsync(x => x.Id == artifactId, ct);

        if (artifact == null)
            return AgentApplyArtifactResult.Fail(artifactId, "Artifact не найден.");

        if (expectedRunId.HasValue && artifact.RunId != expectedRunId.Value)
            return AgentApplyArtifactResult.Fail(artifactId, "Artifact не принадлежит указанному AI-run.", artifact.Run.ConversationId, artifact.RunId);

        if (artifact.Run.Conversation.UserId != currentUserId)
            return AgentApplyArtifactResult.Fail(artifactId, "Нельзя применить artifact из чужого AI-чата.", artifact.Run.ConversationId, artifact.RunId);

        if (!IsSupportedProposalArtifact(artifact.Type))
            return AgentApplyArtifactResult.Fail(artifactId, $"Artifact типа '{artifact.Type}' нельзя применять как правки курса.", artifact.Run.ConversationId, artifact.RunId);

        if ((artifact.DataJson?.Length ?? 0) > MaxArtifactDataJsonChars)
            return AgentApplyArtifactResult.Fail(artifactId, "AI proposal слишком большой для безопасного применения.", artifact.Run.ConversationId, artifact.RunId);

        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(artifact.DataJson) ? "{}" : artifact.DataJson);
        var result = new AgentApplyArtifactResult
        {
            Ok = true,
            DryRun = dryRun,
            ArtifactId = artifact.Id,
            ArtifactType = artifact.Type,
            RunId = artifact.RunId,
            ConversationId = artifact.Run.ConversationId,
            Message = dryRun ? "AI proposal проверен. Изменения не применялись." : "AI proposal применён."
        };

        if (IsSaveHiddenDraftApprovalRequest(artifact.Type, doc.RootElement))
            return await ApplySaveHiddenDraftApprovalAsync(artifact, doc.RootElement, result, currentUserId, currentUserRole, request, now, ct);

        var data = ExtractProposalPayload(artifact.Type, doc.RootElement);
        if (data.ValueKind != JsonValueKind.Object)
            return AgentApplyArtifactResult.Fail(artifactId, "Artifact не содержит JSON-patch правок курса.", artifact.Run.ConversationId, artifact.RunId);

        var courseId = await ResolveCourseIdAsync(data, artifact.Run.Conversation.CourseId, ct);
        if (!courseId.HasValue)
            return result.AsFailed("Не удалось определить courseId для применения AI proposal.");

        result.CourseId = courseId.Value;
        var beforeId = GetGuid(data, "beforeAssignmentId");
        var afterId = GetGuid(data, "afterAssignmentId");
        if (!await DraftPlacementBelongsToCourseAsync(courseId.Value, beforeId, afterId, ct))
            return result.AsFailed("beforeAssignmentId/afterAssignmentId не относятся к выбранному курсу.");
        if (!await _courseAccess.CanEditCourseAsync(currentUserId, currentUserRole, courseId.Value))
            return result.AsFailed("У пользователя нет прав редактирования этого курса.");

        var course = await _db.Courses.FirstOrDefaultAsync(x => x.Id == courseId.Value, ct);
        if (course == null)
            return result.AsFailed("Курс для применения AI proposal не найден.");

        var updatesArray = GetArrayElement(data, "assignments", "updates", "assignmentUpdates");
        var orderIds = ExtractGuidArray(data, "order", "assignmentOrder", "orderedAssignmentIds", "courseOrder");
        var duplicateOrderIds = orderIds.GroupBy(x => x).Where(x => x.Count() > 1).Select(x => x.Key).ToList();
        if (duplicateOrderIds.Count > 0)
            return result.AsFailed("Порядок заданий содержит повторяющиеся id.", new { duplicateOrderIds });
        if (orderIds.Count > MaxOrderIds)
            return result.AsFailed($"AI proposal содержит слишком большой порядок заданий: максимум {MaxOrderIds} id.");
        orderIds = orderIds.Distinct().ToList();

        var courseAssignments = await _db.TaskAssignments
            .Include(x => x.TestCases)
            .Where(x => x.CourseId == courseId.Value)
            .OrderBy(x => x.Sort)
            .ThenBy(x => x.CreatedAt)
            .ToListAsync(ct);
        var byId = courseAssignments.ToDictionary(x => x.Id);

        if (orderIds.Count > 0)
        {
            var foreignOrderIds = orderIds.Where(x => !byId.ContainsKey(x)).ToList();
            if (foreignOrderIds.Count > 0)
                return result.AsFailed("Порядок содержит задания не из выбранного курса.", new { foreignOrderIds });
        }

        var updateItems = new List<JsonElement>();
        if (updatesArray.HasValue)
        {
            foreach (var item in updatesArray.Value.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object)
                    updateItems.Add(item.Clone());
            }
        }

        if (updateItems.Count > MaxAssignmentUpdates)
            return result.AsFailed($"AI proposal содержит слишком много правок заданий: максимум {MaxAssignmentUpdates}.");

        if (!HasApplyIntent(data, updateItems, orderIds))
            return result.AsFailed("AI proposal не содержит применимых изменений курса или заданий.");

        if (!dryRun && !request.Force && await HasSuccessfulApplyStepAsync(artifact.Id, ct))
            return result.AsFailed("Этот AI proposal уже был применён. Повторное применение заблокировано, чтобы не создать случайные дубли/повторные правки. Передайте force=true только если точно нужно применить повторно.", new { artifactId = artifact.Id });

        foreach (var item in updateItems)
        {
            var id = GetGuid(item, "id", "assignmentId");
            if (!id.HasValue)
                return result.AsFailed("Каждый update должен содержать id/assignmentId.");
            if (!byId.TryGetValue(id.Value, out var assignment))
                return result.AsFailed("AI proposal содержит правку задания не из выбранного курса.", new { assignmentId = id.Value });

            var validation = await ValidateAssignmentPatchAsync(assignment, item, ct);
            result.Validations.Add(validation);
            if (!validation.Ok)
                return result.AsFailed(validation.Message, validation);
        }

        var normalizedRatings = 0;
        var reordered = orderIds.Count > 0;

        if (!dryRun)
        {
            try
            {
                await using var tx = await _db.Database.BeginTransactionAsync(ct);

                var coursePatch = ApplyCoursePatch(course, data, now);
                if (coursePatch.ChangedFields.Count > 0)
                    result.Updated.Add(coursePatch);

                foreach (var item in updateItems)
                {
                    var id = GetGuid(item, "id", "assignmentId")!.Value;
                    var assignment = byId[id];
                    var applied = await ApplyAssignmentPatchAsync(assignment, item, now, ct);
                    if (applied.ChangedFields.Count > 0)
                        result.Updated.Add(applied);
                }

                if (orderIds.Count > 0)
                    ApplyCourseOrder(courseAssignments, orderIds, now);
                else
                {
                    var sortOverrides = BuildSortOverrides(updateItems, byId);
                    if (sortOverrides.Count > 0)
                    {
                        reordered = true;
                        ApplyCourseSortOverrides(courseAssignments, sortOverrides, now);
                    }
                }

                normalizedRatings = NormalizeCourseRatingsIfRequested(data, courseAssignments, now);
                if (normalizedRatings > 0)
                    result.Updated.Add(new { type = "rating_normalization", changedCount = normalizedRatings });

                _db.AgentSteps.Add(new AgentStep
                {
                    Id = Guid.NewGuid(),
                    RunId = artifact.RunId,
                    Seq = await NextStepSeqAsync(artifact.RunId, ct),
                    Kind = "course_edit",
                    Status = "completed",
                    ActionName = "apply_course_edit_proposal",
                    Title = "AI-правки курса применены",
                    Summary = $"Применено правок/действий: {result.Updated.Count}. Проверок runner: {result.Validations.Count(x => x.Kind == "code-tests")}." + (string.IsNullOrWhiteSpace(TrimNullable(request.Note, MaxApplyNoteChars)) ? string.Empty : $" Примечание: {TrimNullable(request.Note, MaxApplyNoteChars)}"),
                    InputJson = SafeSerializeForLog(new { artifactId = artifact.Id, artifactType = artifact.Type, note = TrimNullable(request.Note, MaxApplyNoteChars), force = request.Force }),
                    OutputJson = SafeSerializeForLog(result),
                    CreatedAtUtc = now,
                    FinishedAtUtc = now,
                    IsVisibleToUser = true,
                });

                artifact.Run.UpdatedAtUtc = now;
                artifact.Run.Conversation.UpdatedAtUtc = now;
                await _db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
            }
            catch (Exception ex)
            {
                return result.AsFailed($"Не удалось применить AI proposal. Изменения откатились: {ex.Message}");
            }
        }
        else
        {
            var coursePreview = BuildCoursePatchPreview(course, data);
            if (coursePreview.ChangedFields.Count > 0)
                result.Updated.Add(coursePreview);

            foreach (var item in updateItems)
            {
                var id = GetGuid(item, "id", "assignmentId")!.Value;
                var assignment = byId[id];
                var preview = BuildPatchPreview(assignment, item);
                if (preview.ChangedFields.Count > 0)
                    result.Updated.Add(preview);
            }
        }

        result.Reordered = reordered;
        result.NormalizedRatings = normalizedRatings;
        return result;
    }

    private async Task<AgentApplyArtifactResult> ApplySaveHiddenDraftApprovalAsync(
        AgentRunArtifact artifact,
        JsonElement root,
        AgentApplyArtifactResult result,
        Guid currentUserId,
        string? currentUserRole,
        AgentApplyArtifactRequest request,
        DateTime now,
        CancellationToken ct)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("payload", out var data) || data.ValueKind != JsonValueKind.Object)
            return result.AsFailed("approval_request/save_hidden_draft не содержит payload с черновиком задания.");

        var courseId = GetGuid(data, "courseId", "selectedCourseId") ?? artifact.Run.Conversation.CourseId;
        if (!courseId.HasValue)
            return result.AsFailed("Не удалось определить courseId для сохранения скрытого AI-черновика.");

        result.CourseId = courseId.Value;
        var beforeId = GetGuid(data, "beforeAssignmentId");
        var afterId = GetGuid(data, "afterAssignmentId");
        if (!await DraftPlacementBelongsToCourseAsync(courseId.Value, beforeId, afterId, ct))
            return result.AsFailed("beforeAssignmentId/afterAssignmentId не относятся к выбранному курсу.");
        if (!await _courseAccess.CanEditCourseAsync(currentUserId, currentUserRole, courseId.Value))
            return result.AsFailed("У пользователя нет прав редактирования этого курса.");

        var sourceTaskIndex = GetInt(data, "sourceTaskIndex", "source_task_index", "index");
        var title = GetString(data, "title", "assignmentTitle");
        var description = GetString(data, "description", "condition", "body");
        var normalizedTitle = Trim(title ?? string.Empty, 200);
        var difficulty = Math.Clamp(GetInt(data, "difficulty") ?? 1, 1, 3);
        var rating = Math.Max(1, GetInt(data, "rating", "points", "score", "weight") ?? difficulty * 10);

        var existingEntity = await _db.TaskAssignments
            .Where(x =>
                x.CourseId == courseId.Value
                && x.IsAiDraft
                && x.IsHidden
                && (
                    x.SourceAgentArtifactId == artifact.Id
                    || (x.SourceAgentRunId == artifact.RunId
                        && sourceTaskIndex.HasValue
                        && x.SourceAgentTaskIndex == sourceTaskIndex.Value)
                    || x.Title == normalizedTitle))
            .OrderByDescending(x => x.SourceAgentArtifactId == artifact.Id)
            .ThenByDescending(x => x.SourceAgentRunId == artifact.RunId)
            .ThenBy(x => x.Sort)
            .FirstOrDefaultAsync(ct);
        if (existingEntity == null && sourceTaskIndex.HasValue && IsLearningBridgeDraftData(data))
        {
            var bridgeCandidates = await _db.TaskAssignments
                .Where(x => x.CourseId == courseId.Value
                            && x.IsAiDraft
                            && x.IsHidden
                            && x.SourceAgentTaskIndex == sourceTaskIndex.Value)
                .OrderBy(x => x.Sort)
                .ToListAsync(ct);
            existingEntity = bridgeCandidates.FirstOrDefault(x => IsLikelySameLearningBridgeStep(x.Title, normalizedTitle, sourceTaskIndex.Value));
        }
        if (existingEntity != null)
        {
            if (!request.DryRun)
            {
                existingEntity.Title = Trim(title ?? existingEntity.Title, 200);
                existingEntity.Description = ToTiptapDocumentJson(description ?? existingEntity.Title);
                existingEntity.Type = NormalizeDraftAssignmentType(GetString(data, "assignmentType", "assignment_type", "taskType", "task_type"));
                existingEntity.Difficulty = difficulty;
                existingEntity.Rating = rating;
                existingEntity.AllowedLanguagesCsv = existingEntity.Type == "code-test" ? NormalizeRunnerLanguage(GetString(data, "language") ?? "cpp") : (GetString(data, "allowedLanguagesCsv") ?? existingEntity.AllowedLanguagesCsv);
                existingEntity.Tags = MergeCsvTags(GetString(data, "tags"), "AI,черновик");
                existingEntity.SourceAgentRunId = artifact.RunId;
                existingEntity.SourceAgentArtifactId = artifact.Id;
                existingEntity.SourceAgentTaskIndex = sourceTaskIndex;
                existingEntity.AiDraftJson = data.GetRawText();
                existingEntity.UpdatedAt = now;
                existingEntity.PolishedAtUtc = now;
                var replacementTestCases = ExtractTestCases(data).ToList();
                var oldTestCases = await _db.TaskTestCases.Where(x => x.TaskAssignmentId == existingEntity.Id).ToListAsync(ct);
                _db.TaskTestCases.RemoveRange(oldTestCases);
                foreach (var tc in replacementTestCases)
                {
                    _db.TaskTestCases.Add(new TaskTestCase
                    {
                        Id = Guid.NewGuid(),
                        TaskAssignmentId = existingEntity.Id,
                        Input = tc.Input,
                        ExpectedOutput = tc.ExpectedOutput,
                        IsHidden = tc.IsHidden,
                    });
                }
                await ApplyDraftPlacementAsync(existingEntity, beforeId, afterId, now, ct);
                await _db.SaveChangesAsync(ct);
            }

            var existingTestCount = await _db.TaskTestCases.CountAsync(x => x.TaskAssignmentId == existingEntity.Id, ct);
            result.Message = "Скрытый AI-черновик уже существует; повторное создание не требуется.";
            result.Updated.Add(new AgentHiddenDraftApplySummary
            {
                Action = "hidden_assignment_draft_exists",
                Id = existingEntity.Id,
                CourseId = existingEntity.CourseId,
                Title = existingEntity.Title,
                AssignmentType = existingEntity.Type,
                IsHidden = existingEntity.IsHidden,
                LifecycleStatus = existingEntity.LifecycleStatus,
                TestCount = existingTestCount,
                SourceAgentRunId = existingEntity.SourceAgentRunId,
                SourceAgentArtifactId = existingEntity.SourceAgentArtifactId,
                SourceAgentTaskIndex = existingEntity.SourceAgentTaskIndex
            });
            return result;
        }

        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(description))
            return result.AsFailed("Для скрытого AI-черновика нужны title и description.");

        var assignmentType = NormalizeDraftAssignmentType(GetString(data, "assignmentType", "assignment_type", "taskType", "task_type"));
        if (assignmentType == "image-test")
            return result.AsFailed("Создание image-test через save_hidden_draft пока запрещено backend-gate.");

        var language = NormalizeRunnerLanguage(GetString(data, "language") ?? "cpp");
        var testCases = assignmentType == "code-test" ? ExtractTestCases(data).ToList() : new List<DraftTestCase>();
        var referenceSolution = assignmentType == "code-test" ? GetReferenceSolutionForLanguage(data, language) : null;

        if (assignmentType == "code-test")
        {
            var shapeIssue = ValidateCodeTestsShape(testCases);
            if (shapeIssue != null)
                return result.AsFailed(shapeIssue);
            if (string.IsNullOrWhiteSpace(referenceSolution))
                return result.AsFailed("Для code-test нужен referenceSolution/solution.");

            IList<TestResultDto> runResults;
            try
            {
                runResults = await _compiler.RunTestsAsync(new TestRunRequestDto
                {
                    Language = language,
                    Code = referenceSolution,
                    TestCases = testCases.Select(x => new TestCaseDto
                    {
                        Input = x.Input,
                        ExpectedOutput = x.ExpectedOutput,
                        IsHidden = x.IsHidden
                    }).ToList()
                });
            }
            catch (Exception ex)
            {
                return result.AsFailed($"Runner не смог проверить referenceSolution: {ex.Message}");
            }

            var passed = runResults.Count == testCases.Count && runResults.All(x => x.Passed && string.Equals(x.Status, "ok", StringComparison.OrdinalIgnoreCase));
            result.Validations.Add(new AgentPatchValidationResult
            {
                AssignmentId = Guid.Empty,
                AssignmentTitle = title.Trim(),
                Kind = "code-tests",
                Ok = passed,
                Message = passed ? "Reference solution passed all proposed tests." : "Reference solution did not pass all proposed tests; hidden draft was not created.",
                Details = new
                {
                    language,
                    testCount = testCases.Count,
                    passedCount = runResults.Count(x => x.Passed),
                    results = runResults
                }
            });
            if (!passed)
                return result.AsFailed("Reference solution не прошёл предложенные тесты.");
        }

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
            Tags = MergeCsvTags(GetString(data, "tags"), "AI,черновик"),
            IsHidden = true,
            LifecycleStatus = "ready",
            IsAiDraft = true,
            SourceAgentRunId = artifact.RunId,
            SourceAgentArtifactId = artifact.Id,
            SourceAgentTaskIndex = sourceTaskIndex,
            AiDraftJson = data.GetRawText(),
            CreatedAt = now,
            UpdatedAt = now,
            PolishedAtUtc = now,
            PublishedAtUtc = null,
        };

        foreach (var tc in testCases)
        {
            assignment.TestCases.Add(new TaskTestCase
            {
                Id = Guid.NewGuid(),
                Input = tc.Input,
                ExpectedOutput = tc.ExpectedOutput,
                IsHidden = tc.IsHidden,
            });
        }

        result.Updated.Add(new AgentHiddenDraftApplySummary
        {
            Action = "hidden_assignment_draft",
            Id = assignment.Id,
            CourseId = assignment.CourseId,
            Title = assignment.Title,
            AssignmentType = assignment.Type,
            IsHidden = assignment.IsHidden,
            LifecycleStatus = assignment.LifecycleStatus,
            TestCount = testCases.Count,
            SourceAgentRunId = assignment.SourceAgentRunId,
            SourceAgentArtifactId = assignment.SourceAgentArtifactId,
            SourceAgentTaskIndex = assignment.SourceAgentTaskIndex
        });

        if (request.DryRun)
        {
            result.Message = "Скрытый AI-черновик проверен. Запись в БД не выполнялась.";
            return result;
        }

        _db.TaskAssignments.Add(assignment);
        await ApplyDraftPlacementAsync(assignment, beforeId, afterId, now, ct);
        _db.AgentSteps.Add(new AgentStep
        {
            Id = Guid.NewGuid(),
            RunId = artifact.RunId,
            Seq = await NextStepSeqAsync(artifact.RunId, ct),
            Kind = "draft",
            Status = "completed",
            ActionName = "save_hidden_draft_from_approval_request",
            Title = "Создан скрытый AI-черновик задания",
            Summary = $"Создан скрытый черновик: {assignment.Title}",
            InputJson = SafeSerializeForLog(new { artifactId = artifact.Id, artifactType = artifact.Type, note = TrimNullable(request.Note, MaxApplyNoteChars), force = request.Force }),
            OutputJson = SafeSerializeForLog(result),
            CreatedAtUtc = now,
            FinishedAtUtc = now,
            IsVisibleToUser = true,
        });
        artifact.Run.UpdatedAtUtc = now;
        artifact.Run.Conversation.UpdatedAtUtc = now;
        await _db.SaveChangesAsync(ct);

        result.Message = "Скрытый AI-черновик создан.";
        return result;
    }

    private static bool IsSaveHiddenDraftApprovalRequest(string? artifactType, JsonElement root)
        => string.Equals(artifactType, "approval_request", StringComparison.OrdinalIgnoreCase)
           && string.Equals(GetString(root, "operation"), "save_hidden_draft", StringComparison.OrdinalIgnoreCase);

    private static bool IsSupportedProposalArtifact(string? type)
        => string.Equals(type, "course_edit_proposal", StringComparison.OrdinalIgnoreCase)
           || string.Equals(type, "assignment_update_batch", StringComparison.OrdinalIgnoreCase)
           || string.Equals(type, "course_style_update", StringComparison.OrdinalIgnoreCase)
           || string.Equals(type, "approval_request", StringComparison.OrdinalIgnoreCase);

    private static JsonElement ExtractProposalPayload(string artifactType, JsonElement root)
    {
        if (!string.Equals(artifactType, "approval_request", StringComparison.OrdinalIgnoreCase))
            return root;

        var operation = GetString(root, "operation");
        if (!string.Equals(operation, "apply_course_edit", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(operation, "apply_assignment_update_batch", StringComparison.OrdinalIgnoreCase))
            return default;

        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("payload", out var payload) && payload.ValueKind == JsonValueKind.Object)
            return payload;
        return default;
    }

    private async Task<bool> HasSuccessfulApplyStepAsync(Guid artifactId, CancellationToken ct)
    {
        var artifactText = artifactId.ToString("D");
        return await _db.AgentSteps.AsNoTracking().AnyAsync(x =>
            x.ActionName == "apply_course_edit_proposal"
            && x.Status == "completed"
            && x.InputJson != null
            && x.InputJson.Contains(artifactText), ct);
    }

    private static bool HasApplyIntent(JsonElement data, IReadOnlyList<JsonElement> updateItems, IReadOnlyList<Guid> orderIds)
    {
        if (orderIds.Count > 0) return true;
        if (HasAnyProperty(data, "courseTitle", "courseName", "courseDescription", "courseAbout", "isPublic", "public", "normalizeRatings", "normalizeDifficultyRatings", "ratingPolicy", "difficultyRatingPolicy"))
            return true;

        return updateItems.Any(x => HasAnyProperty(x,
            "title", "description", "condition", "body", "type", "assignmentType", "taskType",
            "difficulty", "rating", "points", "score", "weight", "tags",
            "allowedLanguages", "languages", "allowedLanguagesCsv", "publicTests", "hiddenTests", "testCases",
            "testSpec", "questions", "test", "taskTest", "mathSpec", "blocks", "math", "taskMath",
            "imageTestSimilarityThreshold", "similarityThreshold", "sort", "order", "index"));
    }

    private async Task<Guid?> ResolveCourseIdAsync(JsonElement data, Guid? fallbackCourseId, CancellationToken ct)
    {
        var courseId = GetGuid(data, "selectedCourseId", "courseId") ?? fallbackCourseId;
        if (courseId.HasValue) return courseId.Value;

        var updatesArray = GetArrayElement(data, "assignments", "updates", "assignmentUpdates");
        if (updatesArray.HasValue)
        {
            foreach (var item in updatesArray.Value.EnumerateArray())
            {
                var id = GetGuid(item, "id", "assignmentId");
                if (!id.HasValue) continue;
                courseId = await _db.TaskAssignments.AsNoTracking()
                    .Where(x => x.Id == id.Value)
                    .Select(x => (Guid?)x.CourseId)
                    .FirstOrDefaultAsync(ct);
                if (courseId.HasValue) return courseId.Value;
            }
        }

        return null;
    }

    private async Task<AgentPatchValidationResult> ValidateAssignmentPatchAsync(TaskAssignment assignment, JsonElement item, CancellationToken ct)
    {
        var nextType = NormalizeDraftAssignmentType(GetString(item, "type", "assignmentType", "taskType") ?? assignment.Type);
        if (nextType == "image-test" && assignment.Type != "image-test") nextType = assignment.Type;
        var result = new AgentPatchValidationResult
        {
            AssignmentId = assignment.Id,
            AssignmentTitle = assignment.Title,
            Kind = "shape",
            Ok = true,
            Message = "Patch shape is valid."
        };

        if (string.Equals(nextType, "test", StringComparison.OrdinalIgnoreCase)
            && HasAnyProperty(item, "testSpec", "questions", "test", "taskTest"))
        {
            var questions = ExtractDraftTestQuestionSpecs(item).ToList();
            if (questions.Count == 0)
                return result.AsFailed("testSpec", "AI proposal содержит testSpec/questions, но не содержит ни одного валидного вопроса.");
            if (questions.Count > 100)
                return result.AsFailed("testSpec", "AI proposal содержит слишком много вопросов теста: максимум 100.");
        }

        if (string.Equals(nextType, "math", StringComparison.OrdinalIgnoreCase)
            && HasAnyProperty(item, "mathSpec", "blocks", "math", "taskMath"))
        {
            var blocks = ExtractDraftMathBlockSpecs(item).ToList();
            if (blocks.Count == 0)
                return result.AsFailed("mathSpec", "AI proposal содержит mathSpec/blocks, но не содержит ни одного валидного математического блока.");
            if (blocks.Count > 100)
                return result.AsFailed("mathSpec", "AI proposal содержит слишком много математических блоков: максимум 100.");
        }

        if (!string.Equals(nextType, "code-test", StringComparison.OrdinalIgnoreCase)
            || !HasAnyProperty(item, "publicTests", "hiddenTests", "testCases"))
            return result;

        var cases = ExtractTestCases(item).ToList();
        var testShapeIssue = ValidateCodeTestsShape(cases);
        if (testShapeIssue != null)
            return result.AsFailed("code-tests", testShapeIssue);

        var language = NormalizeRunnerLanguage(
            GetString(item, "language")
            ?? FirstListValue(GetStringList(item, "allowedLanguages", "languages"))
            ?? FirstCsvValue(GetString(item, "allowedLanguagesCsv") ?? assignment.AllowedLanguagesCsv)
            ?? "cpp");
        var referenceSolution = GetReferenceSolutionForLanguage(item, language);
        if (string.IsNullOrWhiteSpace(referenceSolution))
            return result.AsFailed("code-tests", "AI proposal меняет тесты code-test, но не содержит referenceSolution/solution для проверки через runner.");
        if (referenceSolution.Length > MaxReferenceSolutionChars)
            return result.AsFailed("code-tests", $"referenceSolution слишком большой: максимум {MaxReferenceSolutionChars} символов.");

        IList<TestResultDto> runResults;
        try
        {
            runResults = await _compiler.RunTestsAsync(new TestRunRequestDto
            {
                Language = language,
                Code = referenceSolution,
                TestCases = cases.Select(x => new TestCaseDto
                {
                    Input = x.Input,
                    ExpectedOutput = x.ExpectedOutput,
                    IsHidden = x.IsHidden
                }).ToList(),
                PolicyForbiddenCalls = JsonDocumentToStringList(assignment.CodeForbiddenCallsJson),
                PolicyRequiredCalls = JsonDocumentToStringList(assignment.CodeRequiredCallsJson)
            });
        }
        catch (Exception ex)
        {
            return result.AsFailed("code-tests", $"Runner не смог проверить referenceSolution: {ex.Message}");
        }

        var passed = runResults.Count == cases.Count && runResults.All(x => x.Passed && string.Equals(x.Status, "ok", StringComparison.OrdinalIgnoreCase));
        return new AgentPatchValidationResult
        {
            AssignmentId = assignment.Id,
            AssignmentTitle = assignment.Title,
            Kind = "code-tests",
            Ok = passed,
            Message = passed ? "Reference solution passed all proposed tests." : "Reference solution did not pass all proposed tests; patch was not applied.",
            Details = new
            {
                language,
                testCount = cases.Count,
                passedCount = runResults.Count(x => x.Passed),
                results = runResults
            }
        };
    }

    private static AgentAppliedCoursePatch ApplyCoursePatch(Course course, JsonElement data, DateTime now)
    {
        var changedFields = new List<string>();

        var title = GetString(data, "courseTitle", "courseName");
        if (!string.IsNullOrWhiteSpace(title) && title.Trim() != course.Title)
        {
            course.Title = Trim(title, 200);
            changedFields.Add("title");
        }

        var description = GetString(data, "courseDescription", "courseAbout");
        if (!string.IsNullOrWhiteSpace(description) && description.Trim() != (course.Description ?? string.Empty).Trim())
        {
            course.Description = Trim(description, 4000);
            changedFields.Add("description");
        }

        var isPublic = GetBool(data, "isPublic", "public");
        if (isPublic.HasValue && isPublic.Value != course.IsPublic)
        {
            course.IsPublic = isPublic.Value;
            changedFields.Add("isPublic");
        }

        if (changedFields.Count > 0)
            course.UpdatedAt = now;

        return new AgentAppliedCoursePatch
        {
            Id = course.Id,
            Title = course.Title,
            ChangedFields = changedFields
        };
    }

    private static AgentAppliedCoursePatch BuildCoursePatchPreview(Course course, JsonElement data)
    {
        var changedFields = new List<string>();
        foreach (var field in new[] { "courseTitle", "courseName", "courseDescription", "courseAbout", "isPublic", "public" })
        {
            if (HasAnyProperty(data, field) && !changedFields.Contains(field))
                changedFields.Add(field);
        }

        return new AgentAppliedCoursePatch
        {
            Id = course.Id,
            Title = course.Title,
            ChangedFields = changedFields
        };
    }

    private async Task<AgentAppliedAssignmentPatch> ApplyAssignmentPatchAsync(TaskAssignment assignment, JsonElement item, DateTime now, CancellationToken ct)
    {
        var changedFields = new List<string>();
        var nextType = NormalizeDraftAssignmentType(GetString(item, "type", "assignmentType", "taskType") ?? assignment.Type);
        if (nextType == "image-test") nextType = assignment.Type == "image-test" ? "image-test" : assignment.Type;

        var title = GetString(item, "title");
        if (!string.IsNullOrWhiteSpace(title) && title.Trim() != assignment.Title)
        {
            assignment.Title = Trim(title, 200);
            changedFields.Add("title");
        }

        var description = GetString(item, "description", "condition", "body");
        if (!string.IsNullOrWhiteSpace(description))
        {
            assignment.Description = ToTiptapDocumentJson(description);
            changedFields.Add("description");
        }

        if (!string.Equals(assignment.Type, nextType, StringComparison.OrdinalIgnoreCase))
        {
            assignment.Type = nextType;
            changedFields.Add("type");
        }

        var difficulty = GetInt(item, "difficulty");
        if (difficulty.HasValue)
        {
            assignment.Difficulty = Math.Clamp(difficulty.Value, 1, 3);
            changedFields.Add("difficulty");
        }

        var rating = GetInt(item, "rating", "points", "score", "weight");
        if (rating.HasValue)
        {
            assignment.Rating = Math.Max(0, rating.Value);
            changedFields.Add("rating");
        }

        var tags = GetString(item, "tags");
        if (tags != null)
        {
            assignment.Tags = Trim(tags, 500);
            changedFields.Add("tags");
        }

        var allowedLanguages = GetStringList(item, "allowedLanguages", "languages");
        var allowedCsv = GetString(item, "allowedLanguagesCsv");
        if (allowedLanguages.Count > 0 || allowedCsv != null)
        {
            assignment.AllowedLanguagesCsv = allowedLanguages.Count > 0
                ? string.Join(",", allowedLanguages.Select(NormalizeRunnerLanguage).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
                : allowedCsv;
            changedFields.Add("allowedLanguages");
        }

        if (assignment.Type == "code-test" && HasAnyProperty(item, "publicTests", "hiddenTests", "testCases"))
        {
            await _db.TaskTestCases.Where(x => x.TaskAssignmentId == assignment.Id).ExecuteDeleteAsync(ct);
            foreach (var tc in ExtractTestCases(item))
            {
                _db.TaskTestCases.Add(new TaskTestCase
                {
                    Id = Guid.NewGuid(),
                    TaskAssignmentId = assignment.Id,
                    Input = tc.Input,
                    ExpectedOutput = tc.ExpectedOutput,
                    IsHidden = tc.IsHidden,
                });
            }
            changedFields.Add("codeTestCases");
        }

        if (assignment.Type == "test" && HasAnyProperty(item, "testSpec", "questions", "test", "taskTest"))
        {
            var questions = ExtractDraftTestQuestionSpecs(item).ToList();
            if (questions.Count == 0)
                throw new ValidationException("AI proposal contains testSpec/questions, but no valid test questions were parsed.");

            await _db.TaskTestSettings.Where(x => x.TaskAssignmentId == assignment.Id).ExecuteDeleteAsync(ct);
            await _db.TaskTestQuestions.Where(x => x.TaskAssignmentId == assignment.Id).ExecuteDeleteAsync(ct);
            AddDraftTestContent(assignment.Id, item, questions, now);
            changedFields.Add("testSpec");
        }

        if (assignment.Type == "math" && HasAnyProperty(item, "mathSpec", "blocks", "math", "taskMath"))
        {
            var blocks = ExtractDraftMathBlockSpecs(item).ToList();
            if (blocks.Count == 0)
                throw new ValidationException("AI proposal contains mathSpec/blocks, but no valid math blocks were parsed.");

            await _db.TaskMathSettings.Where(x => x.TaskAssignmentId == assignment.Id).ExecuteDeleteAsync(ct);
            await _db.TaskMathBlocks.Where(x => x.TaskAssignmentId == assignment.Id).ExecuteDeleteAsync(ct);
            AddDraftMathContent(assignment.Id, item, blocks, now);
            changedFields.Add("mathSpec");
        }

        if (assignment.Type == "image-test")
        {
            var threshold = GetDouble(item, "imageTestSimilarityThreshold", "similarityThreshold");
            if (threshold.HasValue)
            {
                assignment.ImageTestSimilarityThreshold = Math.Clamp(threshold.Value, 0, 100);
                changedFields.Add("imageTestSimilarityThreshold");
            }
        }

        if (changedFields.Count > 0)
            assignment.UpdatedAt = now;

        return new AgentAppliedAssignmentPatch
        {
            Id = assignment.Id,
            Title = assignment.Title,
            Type = assignment.Type,
            ChangedFields = changedFields
        };
    }

    private static AgentAppliedAssignmentPatch BuildPatchPreview(TaskAssignment assignment, JsonElement item)
    {
        var changedFields = new List<string>();
        foreach (var field in new[] { "title", "description", "condition", "body", "type", "assignmentType", "taskType", "difficulty", "rating", "points", "score", "weight", "tags", "allowedLanguages", "languages", "allowedLanguagesCsv", "publicTests", "hiddenTests", "testCases", "testSpec", "questions", "test", "taskTest", "mathSpec", "blocks", "math", "taskMath", "imageTestSimilarityThreshold", "similarityThreshold", "sort", "order", "index" })
        {
            if (HasAnyProperty(item, field) && !changedFields.Contains(field))
                changedFields.Add(field);
        }

        return new AgentAppliedAssignmentPatch
        {
            Id = assignment.Id,
            Title = assignment.Title,
            Type = assignment.Type,
            ChangedFields = changedFields
        };
    }

    private static string? ValidateCodeTestsShape(IReadOnlyList<DraftTestCase> cases)
    {
        if (cases.Count > MaxCodeTestCases)
            return $"Слишком много test cases для безопасной проверки: максимум {MaxCodeTestCases}.";

        if (cases.Any(x => (x.Input?.Length ?? 0) > MaxTestInputChars))
            return $"Слишком большой input в test case: максимум {MaxTestInputChars} символов.";

        if (cases.Any(x => (x.ExpectedOutput?.Length ?? 0) > MaxTestOutputChars))
            return $"Слишком большой expectedOutput в test case: максимум {MaxTestOutputChars} символов.";

        var publicCount = cases.Count(x => !x.IsHidden && x.HasExpectedOutput);
        var hiddenCount = cases.Count(x => x.IsHidden && x.HasExpectedOutput);
        if (publicCount < 2 || hiddenCount < 2)
            return "Для изменения code-test нужно минимум 2 publicTests и 2 hiddenTests с expectedOutput.";

        if (cases.Any(x => !x.HasExpectedOutput))
            return "Каждый test case должен содержать expectedOutput. Пустая строка допустима, если это ожидаемый вывод.";

        var fingerprints = cases.Select(NormalizeTestFingerprint).ToList();
        if (fingerprints.Count != fingerprints.Distinct(StringComparer.Ordinal).Count())
            return "Test cases не должны полностью дублировать друг друга.";

        var normalizedPublic = cases.Where(x => !x.IsHidden).Select(NormalizeTestFingerprint).ToHashSet(StringComparer.Ordinal);
        var normalizedHidden = cases.Where(x => x.IsHidden).Select(NormalizeTestFingerprint).ToHashSet(StringComparer.Ordinal);
        if (normalizedHidden.Count == 0 || normalizedHidden.All(normalizedPublic.Contains))
            return "hiddenTests должны содержать хотя бы один кейс, которого нет в publicTests.";

        return null;
    }

    private static string NormalizeTestFingerprint(DraftTestCase test)
        => $"{(test.Input ?? string.Empty).Replace("\r\n", "\n")}=>{(test.ExpectedOutput ?? string.Empty).Replace("\r\n", "\n")}";

    private async Task<bool> DraftPlacementBelongsToCourseAsync(Guid courseId, Guid? beforeId, Guid? afterId, CancellationToken ct)
    {
        if (!beforeId.HasValue && !afterId.HasValue) return true;
        var ids = new[] { beforeId, afterId }.Where(x => x.HasValue).Select(x => x!.Value).ToList();
        var found = await _db.TaskAssignments.AsNoTracking()
            .Where(x => ids.Contains(x.Id) && x.CourseId == courseId)
            .Select(x => x.Id)
            .ToListAsync(ct);
        return found.Count == ids.Count;
    }

    private static bool IsLearningBridgeDraftData(JsonElement data)
        {
            var extra = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("extra", out var extraElement)
                ? extraElement.GetRawText()
                : string.Empty;
            var text = string.Join(" ",
                GetString(data, "title", "assignmentTitle"),
                GetString(data, "description", "condition", "body"),
                GetString(data, "tags"),
                extra)
                .ToLowerInvariant();
            return text.Contains("learning-bridge")
                   || text.Contains("skillbridge")
                   || text.Contains("introducedskills")
                   || text.Contains("missingbridgeskills")
                   || text.Contains("targetskills")
                   || text.Contains("input-onboarding"); // legacy tag from older AI drafts only
        }

        private static bool IsLikelySameLearningBridgeStep(string? existingTitle, string? candidateTitle, int sourceTaskIndex)
        {
            var existing = NormalizeBridgeText(existingTitle);
            var candidate = NormalizeBridgeText(candidateTitle);
            if (string.IsNullOrWhiteSpace(existing) || string.IsNullOrWhiteSpace(candidate)) return false;
            if (string.Equals(existing, candidate, StringComparison.OrdinalIgnoreCase)) return true;

            var candidateTokens = candidate.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(x => x.Length >= 4)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (candidateTokens.Count == 0) return false;

            var existingTokens = existing.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var overlap = candidateTokens.Count(existingTokens.Contains);
            return overlap >= Math.Min(2, candidateTokens.Count);
        }

        private static string NormalizeBridgeText(string? value)
        {
            var text = (value ?? string.Empty).ToLowerInvariant();
            text = Regex.Replace(text, @"[^a-zа-я0-9+#<>.]+", " ", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            return Regex.Replace(text, @"\s+", " ").Trim();
    }

    private async Task ApplyDraftPlacementAsync(TaskAssignment assignment, Guid? beforeId, Guid? afterId, DateTime now, CancellationToken ct)
    {
        var ordered = await _db.TaskAssignments
            .Where(x => x.CourseId == assignment.CourseId)
            .OrderBy(x => x.Sort)
            .ThenBy(x => x.Id)
            .ToListAsync(ct);

        var tracked = _db.ChangeTracker.Entries<TaskAssignment>()
            .Where(x => x.State != EntityState.Detached
                        && x.State != EntityState.Deleted
                        && x.Entity.CourseId == assignment.CourseId)
            .Select(x => x.Entity)
            .ToList();
        foreach (var trackedAssignment in tracked)
        {
            var existingIndex = ordered.FindIndex(x => x.Id == trackedAssignment.Id);
            if (existingIndex >= 0) ordered[existingIndex] = trackedAssignment;
            else ordered.Add(trackedAssignment);
        }
        ordered = ordered
            .OrderBy(x => x.Sort)
            .ThenBy(x => x.Id)
            .ToList();

        ordered.RemoveAll(x => x.Id == assignment.Id);

        var insertIndex = ordered.Count;
        if (beforeId.HasValue)
        {
            var beforeIndex = ordered.FindIndex(x => x.Id == beforeId.Value);
            if (beforeIndex >= 0)
            {
                insertIndex = beforeIndex;
                if (assignment.SourceAgentTaskIndex.HasValue)
                {
                    var groupStart = beforeIndex;
                    while (groupStart > 0 && IsSameAgentDraftGroup(ordered[groupStart - 1], assignment))
                        groupStart--;

                    insertIndex = groupStart;
                    while (insertIndex < beforeIndex
                           && IsSameAgentDraftGroup(ordered[insertIndex], assignment)
                           && ordered[insertIndex].SourceAgentTaskIndex.GetValueOrDefault(int.MaxValue) <= assignment.SourceAgentTaskIndex.Value)
                        insertIndex++;
                }
            }
        }
        else if (afterId.HasValue)
        {
            var afterIndex = ordered.FindIndex(x => x.Id == afterId.Value);
            if (afterIndex >= 0)
            {
                insertIndex = afterIndex + 1;
                if (assignment.SourceAgentTaskIndex.HasValue)
                {
                    while (insertIndex < ordered.Count
                           && IsSameAgentDraftGroup(ordered[insertIndex], assignment)
                           && ordered[insertIndex].SourceAgentTaskIndex.GetValueOrDefault(int.MaxValue) <= assignment.SourceAgentTaskIndex.Value)
                        insertIndex++;
                }
            }
        }

        ordered.Insert(Math.Clamp(insertIndex, 0, ordered.Count), assignment);
        for (var i = 0; i < ordered.Count; i++)
        {
            ordered[i].Sort = i;
            ordered[i].UpdatedAt = now;
        }
    }

    private static bool IsSameAgentDraftGroup(TaskAssignment existing, TaskAssignment candidate)
    {
        return existing.IsAiDraft
               && candidate.IsAiDraft
               && existing.IsHidden
               && candidate.IsHidden
               && existing.SourceAgentRunId.HasValue
               && candidate.SourceAgentRunId.HasValue
               && existing.SourceAgentRunId == candidate.SourceAgentRunId
               && existing.SourceAgentTaskIndex.HasValue
               && candidate.SourceAgentTaskIndex.HasValue;
    }

    private static Dictionary<Guid, int> BuildSortOverrides(IEnumerable<JsonElement> updateItems, Dictionary<Guid, TaskAssignment> byId)
    {
        var result = new Dictionary<Guid, int>();
        foreach (var item in updateItems)
        {
            var id = GetGuid(item, "id", "assignmentId");
            if (!id.HasValue || !byId.ContainsKey(id.Value)) continue;
            var sort = GetInt(item, "sort", "order", "index");
            if (sort.HasValue)
                result[id.Value] = Math.Max(0, sort.Value);
        }
        return result;
    }

    private async Task<int> NextStepSeqAsync(Guid runId, CancellationToken ct)
        => (await _db.AgentSteps.Where(x => x.RunId == runId).Select(x => (int?)x.Seq).MaxAsync(ct) ?? 0) + 1;

    private static List<string>? JsonDocumentToStringList(JsonDocument? doc)
    {
        if (doc == null || doc.RootElement.ValueKind != JsonValueKind.Array) return null;
        var list = new List<string>();
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            var value = item.ValueKind == JsonValueKind.String ? item.GetString() : item.ToString();
            if (!string.IsNullOrWhiteSpace(value)) list.Add(value);
        }
        return list.Count == 0 ? null : list;
    }

    private static string? FirstCsvValue(string? csv)
        => (csv ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();

    private static string? FirstListValue(IReadOnlyList<string> values)
        => values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));

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
        return result;
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

    private sealed record DraftOptionSpec(string Key, string Text);
    private sealed record DraftTestQuestionSpec(int Order, string Type, string Prompt, List<DraftOptionSpec> Options, List<string> CorrectOptionKeys, List<string> AcceptedAnswers, bool CaseSensitive, bool TrimAnswers);
    private sealed record DraftMathMatchPairSpec(string LeftKey, string RightKey);
    private sealed record DraftMathBlockSpec(int Order, string Kind, string Prompt, string? PromptContentJson, int Score, bool IsRequired, List<DraftOptionSpec> Options, List<string> CorrectOptionKeys, List<string> AcceptedAnswers, bool CaseSensitive, bool TrimAnswers, double? NumericTolerance, List<string> OrderItems, List<DraftOptionSpec> MatchLeftItems, List<DraftOptionSpec> MatchRightItems, List<DraftMathMatchPairSpec> MatchPairs);
    private sealed record DraftTestCase(string Input, string ExpectedOutput, bool IsHidden, bool HasExpectedOutput);

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
        => ExtractNamedOptions(element, "options", "answers", "choices");

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

    private static IEnumerable<DraftTestCase> ExtractTestCases(JsonElement data)
    {
        foreach (var group in new[] { (Name: "publicTests", Hidden: false), (Name: "hiddenTests", Hidden: true), (Name: "testCases", Hidden: false) })
        {
            if (!data.TryGetProperty(group.Name, out var arr) || arr.ValueKind != JsonValueKind.Array) continue;
            foreach (var item in arr.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var input = GetString(item, "input") ?? string.Empty;
                var hasExpectedOutput = item.TryGetProperty("expectedOutput", out var expectedProp)
                                        || item.TryGetProperty("output", out expectedProp);
                var output = hasExpectedOutput && expectedProp.ValueKind != JsonValueKind.Null
                    ? (expectedProp.ValueKind == JsonValueKind.String ? expectedProp.GetString() ?? string.Empty : expectedProp.ToString())
                    : string.Empty;
                var hidden = GetBool(item, "isHidden", "hidden") ?? group.Hidden;
                yield return new DraftTestCase(input, output, hidden, hasExpectedOutput);
            }
        }
    }

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

    private static string? GetString(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var prop)) continue;
            if (prop.ValueKind == JsonValueKind.String) return prop.GetString();
            if (prop.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False) return prop.ToString();
            if (prop.ValueKind == JsonValueKind.Array)
            {
                var values = prop.EnumerateArray()
                    .Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : x.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False ? x.ToString() : null)
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Select(x => x!.Trim())
                    .ToArray();
                if (values.Length > 0) return string.Join(",", values);
            }
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
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
        if (index >= 0 && index < alphabet.Length) return alphabet[index].ToString();
        return $"K{index + 1}";
    }

    private static string MergeCsvTags(string? existing, string required)
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

    private static string ToTiptapDocumentJson(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return JsonSerializer.Serialize(new { type = "doc", content = Array.Empty<object>() });

        try
        {
            using var doc = JsonDocument.Parse(value);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("type", out var type)
                && string.Equals(type.GetString(), "doc", StringComparison.OrdinalIgnoreCase))
                return value;
        }
        catch
        {
        }

        var paragraphs = value.Replace("\r\n", "\n").Split('\n')
            .Select(x => x.Trim())
            .Where(x => x.Length > 0)
            .Select(x => new
            {
                type = "paragraph",
                content = new object[] { new { type = "text", text = x } }
            })
            .Cast<object>()
            .ToArray();

        return JsonSerializer.Serialize(new { type = "doc", content = paragraphs });
    }

    private static string Trim(string value, int max)
    {
        value = value.Trim();
        return value.Length <= max ? value : value[..max];
    }

    private static string SafeSerializeForLog(object? value)
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

    private static string? TrimNullable(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim();
        return value.Length <= max ? value : value[..max];
    }
}

public sealed class AgentHiddenDraftApplySummary
{
    [JsonPropertyName("action")]
    public string Action { get; set; } = string.Empty;

    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("courseId")]
    public Guid CourseId { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("assignmentType")]
    public string AssignmentType { get; set; } = string.Empty;

    [JsonPropertyName("isHidden")]
    public bool IsHidden { get; set; }

    [JsonPropertyName("lifecycleStatus")]
    public string LifecycleStatus { get; set; } = string.Empty;

    [JsonPropertyName("testCount")]
    public int TestCount { get; set; }

    [JsonPropertyName("sourceAgentRunId")]
    public Guid? SourceAgentRunId { get; set; }

    [JsonPropertyName("sourceAgentArtifactId")]
    public Guid? SourceAgentArtifactId { get; set; }

    [JsonPropertyName("sourceAgentTaskIndex")]
    public int? SourceAgentTaskIndex { get; set; }
}

public sealed class AgentApplyArtifactRequest
{
    public bool DryRun { get; set; }
    public bool Force { get; set; }
    public string? Note { get; set; }
}

public sealed class AgentApplyArtifactResult
{
    public bool Ok { get; set; }
    public bool DryRun { get; set; }
    public Guid ArtifactId { get; set; }
    public string? ArtifactType { get; set; }
    public Guid RunId { get; set; }
    public Guid ConversationId { get; set; }
    public Guid? CourseId { get; set; }
    public string Message { get; set; } = string.Empty;
    public bool Reordered { get; set; }
    public int NormalizedRatings { get; set; }
    public List<object> Updated { get; set; } = new();
    public List<AgentPatchValidationResult> Validations { get; set; } = new();
    public object? Error { get; set; }

    public static AgentApplyArtifactResult Fail(Guid artifactId, string message, Guid? conversationId = null, Guid? runId = null)
        => new()
        {
            Ok = false,
            ArtifactId = artifactId,
            ConversationId = conversationId ?? Guid.Empty,
            RunId = runId ?? Guid.Empty,
            Message = message
        };

    public AgentApplyArtifactResult AsFailed(string message, object? error = null)
    {
        Ok = false;
        Message = message;
        Error = error;
        return this;
    }
}

public sealed class AgentPatchValidationResult
{
    public Guid AssignmentId { get; set; }
    public string AssignmentTitle { get; set; } = string.Empty;
    public string Kind { get; set; } = "shape";
    public bool Ok { get; set; } = true;
    public string Message { get; set; } = string.Empty;
    public object? Details { get; set; }

    public AgentPatchValidationResult AsFailed(string kind, string message)
    {
        Kind = kind;
        Ok = false;
        Message = message;
        return this;
    }
}

public sealed class AgentAppliedCoursePatch
{
    public string Entity { get; set; } = "course";
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public List<string> ChangedFields { get; set; } = new();
}

public sealed class AgentAppliedAssignmentPatch
{
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public List<string> ChangedFields { get; set; } = new();
}
