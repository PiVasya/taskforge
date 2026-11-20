using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using TaskForge.Tasks.Api.Data;
using TaskForge.Tasks.Api.Domain;
using TaskForge.Tasks.Api.Services.Ownership;
using TaskForge.Realtime;

using TaskForge.Tasks.Api.Contracts;
using static TaskForge.Tasks.Api.Services.Access.AssignmentApiAccessService;
using static TaskForge.Tasks.Api.Services.Common.AssignmentApiCommonService;
using static TaskForge.Tasks.Api.Services.Image.AssignmentApiImageService;
using static TaskForge.Tasks.Api.Services.Mapping.AssignmentApiMappingService;
using static TaskForge.Tasks.Api.Services.Math.AssignmentApiMathService;
using static TaskForge.Tasks.Api.Services.Results.AssignmentApiResultsService;
using static TaskForge.Tasks.Api.Services.Serialization.AssignmentApiSerializationService;

namespace TaskForge.Tasks.Api.Services.Testing;

internal static class AssignmentApiTestingService
{
    internal static async Task<IResult> StartTest(Guid assignmentId, HttpContext http, IConfiguration cfg, TasksDbContext db, IHttpClientFactory clients, CancellationToken ct)
    {
        var userId = RequireUser(http, cfg);
        if (userId == null) return Unauthorized();
        var assignment = await db.Assignments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == assignmentId, ct);
        if (assignment == null || !await CanUserAccessAssignmentAsync(assignment, http, cfg, db, clients, ct)) return Microsoft.AspNetCore.Http.Results.NotFound(new { message = "Задание не найдено.", code = "ASSIGNMENT_NOT_FOUND" });
        if (!string.Equals(assignment.Type, "test", StringComparison.OrdinalIgnoreCase)) return Microsoft.AspNetCore.Http.Results.Conflict(new { message = "Задание не является обычным тестом.", code = "TEST_ASSIGNMENT_TYPE_REQUIRED" });
        var (spec, revision) = await TestAssignmentSpecService.ReadVersionedAsync(db, assignment, ct);
        if (spec.Questions.Count == 0) return Problem(400, "TEST_HAS_NO_QUESTIONS", "tasks.test.start", "В тесте пока нет вопросов.");
        var ignoreTimeLimit = IgnoreAiTaskAttemptTimeLimits(http, cfg);
        var active = await InteractiveAttemptClient.ActiveAsync(clients, cfg, "test", userId.Value, assignmentId, ct);
        if (active != null)
        {
            var existingUnlimited = spec.Settings.UnlimitedAttempts || HasUnlimitedAiTaskAttempts(http, cfg);
            var previousVersion = active.UpdatedAt;
            var limitChanged = RefreshTimeLimit(active, spec, ignoreTimeLimit, revision);
            var orderChanged = TestAttemptQuestionOrder.Refresh(active, spec);
            if (orderChanged || limitChanged)
                active = await InteractiveAttemptClient.SaveDraftAsync(clients, cfg, active, previousVersion, ct);
            return Microsoft.AspNetCore.Http.Results.Ok(TestStartDto(active, spec, existingUnlimited, ignoreTimeLimit, revision));
        }
        var unlimitedAttempts = spec.Settings.UnlimitedAttempts || HasUnlimitedAiTaskAttempts(http, cfg);
        var used = (int)(await InteractiveAttemptClient.SearchAsync(clients, cfg,
            new InteractiveAttemptClient.AttemptSearch("test", userId.Value, assignmentId, Take: 1), ct)).Total;
        var max = unlimitedAttempts ? int.MaxValue : spec.Settings.MaxAttempts;
        if (used + 1 > max) return Microsoft.AspNetCore.Http.Results.Json(new { message = "Достигнут лимит попыток.", code = "ATTEMPT_LIMIT_REACHED" }, statusCode: StatusCodes.Status409Conflict);
        if (await ConsumeTaskEnergyAsync(http, cfg, clients, userId.Value, "test-attempt-start", ct) is { } quotaProblem) return quotaProblem;
        var attempt = new TaskAttempt { Kind = "test", TaskAssignmentId = assignmentId, UserId = userId.Value, AttemptNumber = used + 1, TimeLimitSeconds = ignoreTimeLimit ? null : TimeLimitFor(spec.Settings.AttemptTimeLimitsSeconds, used + 1) };
        attempt.OrderJson = TestAttemptQuestionOrder.Create(OrderedIds(spec.Questions.Select(x => x.Id), spec.Settings.ShuffleQuestions, attempt.Id));
        try
        {
            var result = await InteractiveAttemptClient.StartAsync(clients, cfg, attempt, max,
                spec.Settings.AttemptTimeLimitsSeconds.ToArray(), ignoreTimeLimit, ct);
            if (!result.Created)
                await RefundTaskEnergyAsync(http, cfg, clients, userId.Value, "test-attempt-already-active", CancellationToken.None);
            return Microsoft.AspNetCore.Http.Results.Ok(TestStartDto(result.Attempt, spec, unlimitedAttempts, ignoreTimeLimit, revision));
        }
        catch (InteractiveAttemptConflictException ex) when (ex.Code == "ATTEMPT_LIMIT_REACHED")
        {
            await RefundTaskEnergyAsync(http, cfg, clients, userId.Value, "test-attempt-limit", CancellationToken.None);
            return Microsoft.AspNetCore.Http.Results.Conflict(new { code = "ATTEMPT_LIMIT_REACHED", message = "Достигнут лимит попыток." });
        }
        catch
        {
            await RefundTaskEnergyAsync(http, cfg, clients, userId.Value, "test-attempt-start-failed", CancellationToken.None);
            throw;
        }
    }

    internal static async Task<IResult> SubmitTest(Guid assignmentId, JsonElement payload, HttpContext http, IConfiguration cfg, TasksDbContext db, IHttpClientFactory clients, CancellationToken ct)
    {
        var userId = RequireUser(http, cfg);
        if (userId == null) return Unauthorized();
        var attemptId = GuidProp(payload, "attemptId");
        if (attemptId == Guid.Empty) return Problem(400, "ATTEMPT_ID_REQUIRED", "tasks.test.submit", "Не передан attemptId.");
        var attempt = await InteractiveAttemptClient.FindAsync(clients, cfg, attemptId, ct);
        if (attempt == null || attempt.Kind != "test" || attempt.TaskAssignmentId != assignmentId || attempt.UserId != userId.Value) return Microsoft.AspNetCore.Http.Results.NotFound(new { message = "Попытка не найдена.", code = "ATTEMPT_NOT_FOUND" });
        if (attempt.SubmittedAt != null) return Problem(400, "ATTEMPT_ALREADY_SUBMITTED", "tasks.test.submit", "Эта попытка уже была отправлена.");
        var assignment = await db.Assignments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == assignmentId, ct);
        if (assignment == null || !await CanUserAccessAssignmentAsync(assignment, http, cfg, db, clients, ct)) return Microsoft.AspNetCore.Http.Results.NotFound(new { message = "Задание не найдено.", code = "ASSIGNMENT_NOT_FOUND" });
        if (!string.Equals(assignment.Type, "test", StringComparison.OrdinalIgnoreCase)) return Microsoft.AspNetCore.Http.Results.Conflict(new { message = "Задание не является обычным тестом.", code = "TEST_ASSIGNMENT_TYPE_REQUIRED" });
        var (spec, revision) = await TestAssignmentSpecService.ReadVersionedAsync(db, assignment, ct);
        if (spec.Questions.Count == 0)
            return Microsoft.AspNetCore.Http.Results.Conflict(new { code = "TEST_HAS_NO_QUESTIONS", message = "В тесте пока нет вопросов." });
        var ignoreTimeLimit = IgnoreAiTaskAttemptTimeLimits(http, cfg);
        var previousVersion = attempt.UpdatedAt;
        RefreshTimeLimit(attempt, spec, ignoreTimeLimit, revision);
        TestAttemptQuestionOrder.Refresh(attempt, spec);
        var order = TestAttemptQuestionOrder.Resolve(attempt, spec);
        var unlimitedAttempts = spec.Settings.UnlimitedAttempts || HasUnlimitedAiTaskAttempts(http, cfg);
        var clientRevision = StringProp(payload, "testRevision");
        if (!string.IsNullOrWhiteSpace(clientRevision) && !string.Equals(clientRevision, revision, StringComparison.Ordinal))
        {
            attempt = await InteractiveAttemptClient.SaveDraftAsync(clients, cfg, attempt, previousVersion, ct);
            return Microsoft.AspNetCore.Http.Results.Json(new
            {
                code = "TEST_UPDATED_RELOAD_REQUIRED",
                message = "Тест изменился. Перед отправкой ответов загрузите актуальные вопросы.",
                latestTest = TestStartDto(attempt, spec, unlimitedAttempts, ignoreTimeLimit, revision)
            }, statusCode: StatusCodes.Status409Conflict);
        }
        var answers = AnswersArray(payload, "answers");
        var byAnswer = answers.GroupBy(x => x.QuestionId).ToDictionary(x => x.Key, x => x.First());
        var map = spec.Questions.ToDictionary(x => x.Id);
        var total = 0; var correct = 0;
        var review = new JsonArray();
        foreach (var id in order)
        {
            if (!map.TryGetValue(id, out var q)) continue;
            total++;
            byAnswer.TryGetValue(id, out var ans);
            var ok = IsTestCorrect(q, ans);
            if (ok) correct++;
            review.Add(TestQuestionReviewNode(q, ans, ok));
        }
        var score = total == 0 ? 0 : (int)System.Math.Floor(correct * 100.0 / total);
        attempt.SubmittedAt = DateTimeOffset.UtcNow;
        attempt.TimeExpired = !ignoreTimeLimit && IsTimeExpired(attempt);
        attempt.TotalUnits = total; attempt.CorrectUnits = correct; attempt.TotalScore = total; attempt.EarnedScore = correct;
        var outcome = ResolveTimedAttemptOutcome(score, spec.Settings.PassPercent, attempt.TimeExpired);
        attempt.ScorePercent = outcome.ScorePercent;
        attempt.Passed = outcome.Passed;
        attempt.AnswersJson = JsonSerializer.Serialize(answers, JsonOptions());
        attempt.ReviewJson = null;
        attempt.UpdatedAt = DateTimeOffset.UtcNow;
        attempt = await InteractiveAttemptClient.SubmitAsync(clients, cfg, attempt, previousVersion, ct);

        TaskForgeDebugTrace.Map("TEST_ATTEMPT_SAVED",
            ("user", userId.Value),
            ("assignment", assignmentId),
            ("attempt", attempt.Id),
            ("attemptNumber", attempt.AttemptNumber),
            ("scorePercent", attempt.ScorePercent),
            ("passed", attempt.Passed),
            ("timeExpired", attempt.TimeExpired),
            ("submittedAt", attempt.SubmittedAt));
        return Microsoft.AspNetCore.Http.Results.Ok(TestSubmissionDto(attempt, spec.Settings, total, correct, unlimitedAttempts, spec.Settings.AllowReview, review));
    }

    internal static object TestStartDto(TaskAttempt attempt, TaskSpec spec, bool unlimitedAttempts = false, bool ignoreTimeLimit = false, string? revision = null)
    {
        var order = TestAttemptQuestionOrder.Resolve(attempt, spec);
        var map = spec.Questions.ToDictionary(x => x.Id);
        var questions = order.Select(id => map[id]).Select(x => TestQuestionPublicDto(x, spec.Settings.ShuffleAnswers, attempt.Id)).ToList();
        var timeLimitSeconds = ignoreTimeLimit ? null : attempt.TimeLimitSeconds;
        return new { attemptId = attempt.Id, attempt.AttemptNumber, maxAttempts = unlimitedAttempts ? 0 : spec.Settings.MaxAttempts, unlimitedAttempts, passPercent = spec.Settings.PassPercent, testRevision = revision, timeLimitSeconds, attemptTimeLimitSeconds = timeLimitSeconds, startedAt = attempt.StartedAt, startedAtUtc = attempt.StartedAt, spec.Settings.ShuffleQuestions, spec.Settings.ShuffleAnswers, questions };
    }

    internal static bool RefreshTimeLimit(TaskAttempt attempt, TaskSpec current, bool ignoreTimeLimit, string revision)
    {
        var changed = false;
        // A new live test must grant the learner its full time limit, even when
        // the question IDs did not change. No new attempt or energy is consumed.
        if (long.TryParse(revision, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var ticks)
            && ticks > attempt.UpdatedAt.ToUniversalTime().Ticks)
        {
            attempt.StartedAt = DateTimeOffset.UtcNow;
            attempt.TimeExpired = false;
            changed = true;
        }
        if (!ignoreTimeLimit)
        {
            var latest = TimeLimitFor(current.Settings.AttemptTimeLimitsSeconds, attempt.AttemptNumber);
            if (attempt.TimeLimitSeconds != latest)
            {
                attempt.TimeLimitSeconds = latest;
                changed = true;
            }
        }
        if (changed) attempt.UpdatedAt = DateTimeOffset.UtcNow;
        return changed;
    }

    // Historical scores remain stored, but question presentation is always current.
    internal static JsonArray CurrentTestReview(TaskAttempt attempt, TaskSpec spec)
    {
        List<TestAnswer> answers;
        try { answers = JsonSerializer.Deserialize<List<TestAnswer>>(attempt.AnswersJson ?? "[]", JsonOptions()) ?? []; }
        catch (JsonException) { answers = []; }
        var byAnswer = answers.Where(x => x.QuestionId != Guid.Empty)
            .GroupBy(x => x.QuestionId).ToDictionary(x => x.Key, x => x.First());
        var map = spec.Questions.ToDictionary(x => x.Id);
        var review = new JsonArray();
        foreach (var id in TestAttemptQuestionOrder.Resolve(attempt, spec))
        {
            var question = map[id];
            byAnswer.TryGetValue(id, out var answer);
            review.Add(TestQuestionReviewNode(question, answer, IsTestCorrect(question, answer)));
        }
        return review;
    }

    internal static object TestSubmissionDto(TaskAttempt attempt, TestSettings settings, int total, int correct,
        bool unlimitedAttempts, bool allowReview, JsonArray review) => new
    {
        attemptId = attempt.Id,
        attempt.AttemptNumber,
        maxAttempts = unlimitedAttempts ? 0 : settings.MaxAttempts,
        unlimitedAttempts,
        passPercent = settings.PassPercent,
        totalQuestions = total,
        correctQuestions = correct,
        scorePercent = attempt.ScorePercent,
        attempt.TimeExpired,
        attempt.Passed,
        allowReview,
        questions = allowReview ? review : new JsonArray()
    };

    internal static bool IsTestCorrect(TestQuestion q, TestAnswer? a)
    {
        if (a is null) return false;
        var type = q.Type.ToLowerInvariant();
        if (type is "single-choice" or "multi-choice")
        {
            // Some API clients send the legacy scalar field for single-choice answers,
            // while others send the list form used by multi-choice. An explicitly empty
            // list must not erase a valid scalar answer.
            var selected = a.SelectedOptionKeys is { Count: > 0 } keys
                ? keys
                : string.IsNullOrWhiteSpace(a.SelectedOptionKey)
                    ? []
                    : [a.SelectedOptionKey!];
            if (selected.Count == 0) return false;
            return SetEq(selected, q.CorrectOptionKeys);
        }
        if (type is "fill" or "text")
        {
            if (string.IsNullOrWhiteSpace(a.Text)) return false;
            return TextAccepted(a.Text, q.AcceptedAnswers, q.CaseSensitive, q.Trim);
        }
        return false;
    }

    internal static JsonElement? PublicTestsJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                return JsonSerializer.SerializeToElement(root.EnumerateArray().Where(IsPublicTest).Select(SanitizePublicTest).ToArray(), JsonOptions());
            }
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("publicTests", out var publicTests) && publicTests.ValueKind == JsonValueKind.Array)
                {
                    return JsonSerializer.SerializeToElement(publicTests.EnumerateArray().Select(SanitizePublicTest).ToArray(), JsonOptions());
                }
                foreach (var name in new[] { "testCases", "tests", "cases" })
                {
                    if (root.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array)
                    {
                        return JsonSerializer.SerializeToElement(arr.EnumerateArray().Where(IsPublicTest).Select(SanitizePublicTest).ToArray(), JsonOptions());
                    }
                }
                return null;
            }
        }
        catch { }
        return null;
    }

    internal static bool IsPublicTest(JsonElement t)
    {
        if (t.ValueKind != JsonValueKind.Object) return false;
        if (BoolProp(t, "isHidden") || BoolProp(t, "hidden")) return false;
        return true;
    }

    internal static object SanitizePublicTest(JsonElement t)
    {
        var input = StringProp(t, "input") ?? StringProp(t, "stdin") ?? string.Empty;
        var expected = StringProp(t, "expectedOutput") ?? StringProp(t, "expected") ?? StringProp(t, "stdout") ?? string.Empty;
        var threshold = IntProp(t, "threshold") ?? IntProp(t, "thresholdPercent") ?? 90;
        var hasExpectedImage = !string.IsNullOrWhiteSpace(StringProp(t, "expectedImageKey") ?? StringProp(t, "referenceKey") ?? StringProp(t, "imageKey") ?? StringProp(t, "imageTestReferenceKey") ?? StringProp(t, "expectedImageBase64") ?? StringProp(t, "referenceBase64") ?? StringProp(t, "imageBase64"));
        // Never expose judge storage keys, private URLs or inline image payloads to the student-facing DTO.
        return new { input, expectedOutput = expected, threshold, isHidden = false, hasExpectedImage };
    }

    internal static JsonElement? PickTestsElement(JsonElement source, string type)
    {
        if (type == "test")
        {
            if ((TryGetPropertyLoose(source, "questions", out var questions) && questions.ValueKind == JsonValueKind.Array) ||
                (TryGetPropertyLoose(source, "testSettings", out var testSettings) && testSettings.ValueKind == JsonValueKind.Object) ||
                (TryGetPropertyLoose(source, "quizSettings", out var quizSettings) && quizSettings.ValueKind == JsonValueKind.Object) ||
                TryGetPropertyLoose(source, "maxAttempts", out _) ||
                TryGetPropertyLoose(source, "passPercent", out _))
            {
                return WrapInteractiveSpec(source, "questions", isMath: false);
            }

            foreach (var name in new[] { "testSpec", "taskTest", "quiz", "tests", "spec", "testCases" })
            {
                if (TryGetPropertyLoose(source, name, out var v) && v.ValueKind is JsonValueKind.Object or JsonValueKind.Array) return v.Clone();
            }
        }

        if (type == "math")
        {
            if ((TryGetPropertyLoose(source, "blocks", out var blocks) && blocks.ValueKind == JsonValueKind.Array) ||
                (TryGetPropertyLoose(source, "testSettings", out var testSettings) && testSettings.ValueKind == JsonValueKind.Object) ||
                (TryGetPropertyLoose(source, "mathSettings", out var mathSettings) && mathSettings.ValueKind == JsonValueKind.Object) ||
                TryGetPropertyLoose(source, "maxAttempts", out _) ||
                TryGetPropertyLoose(source, "passPercent", out _))
            {
                return WrapInteractiveSpec(source, "blocks", isMath: true);
            }

            foreach (var name in new[] { "mathSpec", "mathTask", "math", "tests", "spec", "testCases" })
            {
                if (TryGetPropertyLoose(source, name, out var v) && v.ValueKind is JsonValueKind.Object or JsonValueKind.Array) return v.Clone();
            }
        }

        if (type == "image-test")
        {
            foreach (var name in new[] { "testCases", "cases", "publicTests", "hiddenTests", "imageSpec", "imageTest", "tests" })
            {
                if (TryGetPropertyLoose(source, name, out var v) && v.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                {
                    return name is "publicTests" or "hiddenTests" ? WrapImageTests(source) : v.Clone();
                }
            }
        }

        foreach (var name in new[] { "testCases", "cases", "publicTests", "hiddenTests", "tests" })
        {
            if (TryGetPropertyLoose(source, name, out var v) && v.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            {
                return name is "publicTests" or "hiddenTests" ? WrapCodeTests(source) : v.Clone();
            }
        }

        return null;
    }

    internal static JsonElement WrapCodeTests(JsonElement source)
    {
        var node = new JsonObject();
        if (TryGetPropertyLoose(source, "publicTests", out var publicTests) && publicTests.ValueKind == JsonValueKind.Array) node["publicTests"] = JsonNode.Parse(publicTests.GetRawText());
        if (TryGetPropertyLoose(source, "hiddenTests", out var hiddenTests) && hiddenTests.ValueKind == JsonValueKind.Array) node["hiddenTests"] = JsonNode.Parse(hiddenTests.GetRawText());
        return JsonSerializer.SerializeToElement(node, JsonOptions());
    }

    internal static object TestQuestionPublicDto(TestQuestion question, bool shuffleAnswers, Guid seed)
    {
        var opts = question.Options.ToList();
        if (shuffleAnswers) Shuffle(opts, seed);
        return new
        {
            question.Id,
            question.Order,
            question.Type,
            question.Prompt,
            question.PromptContentJson,
            question.OptionsLayout,
            options = question.Type is "single-choice" or "multi-choice"
                ? opts.Select(option => new { option.Key, option.Text, option.ContentJson }).ToList()
                : null
        };
    }

    internal static JsonObject TestQuestionReviewNode(TestQuestion question, TestAnswer? answer, bool isCorrect) => new()
    {
        ["id"] = question.Id.ToString(),
        ["order"] = question.Order,
        ["type"] = question.Type,
        ["prompt"] = question.Prompt,
        ["promptContentJson"] = question.PromptContentJson,
        ["explanationContentJson"] = question.ExplanationContentJson,
        ["optionsLayout"] = question.OptionsLayout,
        ["options"] = JsonSerializer.SerializeToNode(question.Options, JsonOptions()),
        ["correctOptionKeys"] = JsonSerializer.SerializeToNode(question.CorrectOptionKeys, JsonOptions()),
        ["acceptedAnswers"] = JsonSerializer.SerializeToNode(question.AcceptedAnswers, JsonOptions()),
        ["userAnswer"] = JsonSerializer.SerializeToNode(answer, JsonOptions()),
        ["isCorrect"] = isCorrect
    };

}
