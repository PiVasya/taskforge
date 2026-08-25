using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using TaskForge.Tasks.Api.Data;
using TaskForge.Tasks.Api.Domain;

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
        var spec = ReadTaskSpec(assignment);
        if (spec.Questions.Count == 0) return Problem(400, "TEST_HAS_NO_QUESTIONS", "tasks.test.start", "В тесте пока нет вопросов.");
        var unlimitedAttempts = HasUnlimitedAiTaskAttempts(http, cfg);
        var ignoreTimeLimit = IgnoreAiTaskAttemptTimeLimits(http, cfg);
        var active = await db.Attempts.FirstOrDefaultAsync(x => x.Kind == "test" && x.TaskAssignmentId == assignmentId && x.UserId == userId.Value && x.SubmittedAt == null);
        if (active != null) return Microsoft.AspNetCore.Http.Results.Ok(TestStartDto(active, spec, unlimitedAttempts, ignoreTimeLimit));
        var used = await db.Attempts.CountAsync(x => x.Kind == "test" && x.TaskAssignmentId == assignmentId && x.UserId == userId.Value);
        var max = unlimitedAttempts || spec.Settings.MaxAttempts <= 0 ? int.MaxValue : spec.Settings.MaxAttempts;
        if (used + 1 > max) return Microsoft.AspNetCore.Http.Results.Json(new { message = "Достигнут лимит попыток.", code = "ATTEMPT_LIMIT_REACHED" }, statusCode: StatusCodes.Status409Conflict);
        if (await ConsumeTaskEnergyAsync(http, cfg, clients, userId.Value, "test-attempt-start", ct) is { } quotaProblem) return quotaProblem;
        var attempt = new TaskAttempt { Kind = "test", TaskAssignmentId = assignmentId, UserId = userId.Value, AttemptNumber = used + 1, TimeLimitSeconds = ignoreTimeLimit ? null : TimeLimitFor(spec.Settings.AttemptTimeLimitsSeconds, used + 1) };
        attempt.OrderJson = JsonSerializer.Serialize(OrderedIds(spec.Questions.Select(x => x.Id), spec.Settings.ShuffleQuestions, attempt.Id), JsonOptions());
        db.Attempts.Add(attempt);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch
        {
            await RefundTaskEnergyAsync(http, cfg, clients, userId.Value, "test-attempt-start-failed", CancellationToken.None);
            throw;
        }
        return Microsoft.AspNetCore.Http.Results.Ok(TestStartDto(attempt, spec, unlimitedAttempts, ignoreTimeLimit));
    }

    internal static async Task<IResult> SubmitTest(Guid assignmentId, JsonElement payload, HttpContext http, IConfiguration cfg, TasksDbContext db, IHttpClientFactory clients, CancellationToken ct)
    {
        var userId = RequireUser(http, cfg);
        if (userId == null) return Unauthorized();
        var attemptId = GuidProp(payload, "attemptId");
        if (attemptId == Guid.Empty) return Problem(400, "ATTEMPT_ID_REQUIRED", "tasks.test.submit", "Не передан attemptId.");
        var attempt = await db.Attempts.FirstOrDefaultAsync(x => x.Id == attemptId && x.Kind == "test" && x.TaskAssignmentId == assignmentId && x.UserId == userId.Value);
        if (attempt == null) return Microsoft.AspNetCore.Http.Results.NotFound(new { message = "Попытка не найдена.", code = "ATTEMPT_NOT_FOUND" });
        if (attempt.SubmittedAt != null) return Problem(400, "ATTEMPT_ALREADY_SUBMITTED", "tasks.test.submit", "Эта попытка уже была отправлена.");
        var assignment = await db.Assignments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == assignmentId, ct);
        if (assignment == null || !await CanUserAccessAssignmentAsync(assignment, http, cfg, db, clients, ct)) return Microsoft.AspNetCore.Http.Results.NotFound(new { message = "Задание не найдено.", code = "ASSIGNMENT_NOT_FOUND" });
        var spec = ReadTaskSpec(assignment);
        var answers = AnswersArray(payload, "answers");
        var byAnswer = answers.GroupBy(x => x.QuestionId).ToDictionary(x => x.Key, x => x.First());
        var order = ParseGuidList(attempt.OrderJson);
        if (order.Count == 0) order = spec.Questions.Select(x => x.Id).ToList();
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
        var ignoreTimeLimit = IgnoreAiTaskAttemptTimeLimits(http, cfg);
        var unlimitedAttempts = HasUnlimitedAiTaskAttempts(http, cfg);
        attempt.SubmittedAt = DateTimeOffset.UtcNow;
        attempt.TimeExpired = !ignoreTimeLimit && IsTimeExpired(attempt);
        attempt.TotalUnits = total; attempt.CorrectUnits = correct; attempt.TotalScore = total; attempt.EarnedScore = correct;
        attempt.ScorePercent = attempt.TimeExpired ? 0 : score;
        attempt.Passed = !attempt.TimeExpired && attempt.ScorePercent >= spec.Settings.PassPercent;
        attempt.AnswersJson = JsonSerializer.Serialize(answers, JsonOptions());
        attempt.ReviewJson = new JsonObject { ["questions"] = review }.ToJsonString(JsonOptions());
        attempt.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        TaskForgeDebugTrace.Map("TEST_ATTEMPT_SAVED",
            ("user", userId.Value),
            ("assignment", assignmentId),
            ("attempt", attempt.Id),
            ("attemptNumber", attempt.AttemptNumber),
            ("scorePercent", attempt.ScorePercent),
            ("passed", attempt.Passed),
            ("timeExpired", attempt.TimeExpired),
            ("submittedAt", attempt.SubmittedAt));
        await MarkRatingDirtyInSolutionsAsync(clients, cfg, new[] { userId.Value }, "test-attempt-submitted", assignmentId, ct);
        return Microsoft.AspNetCore.Http.Results.Ok(new { attemptId = attempt.Id, attempt.AttemptNumber, maxAttempts = unlimitedAttempts ? 0 : spec.Settings.MaxAttempts, passPercent = spec.Settings.PassPercent, totalQuestions = total, correctQuestions = correct, scorePercent = attempt.ScorePercent, attempt.TimeExpired, attempt.Passed });
    }

    internal static object TestStartDto(TaskAttempt attempt, TaskSpec spec, bool unlimitedAttempts = false, bool ignoreTimeLimit = false)
    {
        var order = ParseGuidList(attempt.OrderJson);
        var map = spec.Questions.ToDictionary(x => x.Id);
        var questions = (order.Count == 0 ? spec.Questions : order.Where(map.ContainsKey).Select(id => map[id])).Select(x => TestQuestionPublicDto(x, spec.Settings.ShuffleAnswers, attempt.Id)).ToList();
        return new { attemptId = attempt.Id, attempt.AttemptNumber, maxAttempts = unlimitedAttempts ? 0 : spec.Settings.MaxAttempts, passPercent = spec.Settings.PassPercent, attemptTimeLimitSeconds = ignoreTimeLimit ? null : attempt.TimeLimitSeconds, startedAt = attempt.StartedAt, startedAtUtc = attempt.StartedAt, spec.Settings.ShuffleQuestions, spec.Settings.ShuffleAnswers, questions };
    }

    internal static bool IsTestCorrect(TestQuestion q, TestAnswer? a)
    {
        var type = q.Type.ToLowerInvariant();
        if (type is "single-choice" or "multi-choice")
        {
            // Some API clients send the legacy scalar field for single-choice answers,
            // while others send the list form used by multi-choice. An explicitly empty
            // list must not erase a valid scalar answer.
            var selected = a?.SelectedOptionKeys is { Count: > 0 } keys
                ? keys
                : string.IsNullOrWhiteSpace(a?.SelectedOptionKey)
                    ? []
                    : [a.SelectedOptionKey!];
            return SetEq(selected, q.CorrectOptionKeys);
        }
        if (type is "fill" or "text") return TextAccepted(a?.Text, q.AcceptedAnswers, q.CaseSensitive, q.Trim);
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
            options = question.Type is "single-choice" or "multi-choice" ? opts : null
        };
    }

    internal static JsonObject TestQuestionReviewNode(TestQuestion question, TestAnswer? answer, bool isCorrect) => new()
    {
        ["id"] = question.Id.ToString(),
        ["order"] = question.Order,
        ["type"] = question.Type,
        ["prompt"] = question.Prompt,
        ["options"] = JsonSerializer.SerializeToNode(question.Options, JsonOptions()),
        ["correctOptionKeys"] = JsonSerializer.SerializeToNode(question.CorrectOptionKeys, JsonOptions()),
        ["acceptedAnswers"] = JsonSerializer.SerializeToNode(question.AcceptedAnswers, JsonOptions()),
        ["userAnswer"] = JsonSerializer.SerializeToNode(answer, JsonOptions()),
        ["isCorrect"] = isCorrect
    };

}
