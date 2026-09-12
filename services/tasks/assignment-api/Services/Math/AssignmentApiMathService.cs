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
using static TaskForge.Tasks.Api.Services.Results.AssignmentApiResultsService;
using static TaskForge.Tasks.Api.Services.Serialization.AssignmentApiSerializationService;
using static TaskForge.Tasks.Api.Services.Testing.AssignmentApiTestingService;

namespace TaskForge.Tasks.Api.Services.Math;

internal static class AssignmentApiMathService
{
    internal static async Task<IResult> StartMath(Guid assignmentId, HttpContext http, IConfiguration cfg, TasksDbContext db, IHttpClientFactory clients, CancellationToken ct)
    {
        var userId = RequireUser(http, cfg);
        if (userId == null) return Unauthorized();
        var assignment = await db.Assignments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == assignmentId, ct);
        if (assignment == null || !await CanUserAccessAssignmentAsync(assignment, http, cfg, db, clients, ct)) return Microsoft.AspNetCore.Http.Results.NotFound(new { message = "Задание не найдено.", code = "ASSIGNMENT_NOT_FOUND" });
        var spec = ReadMathSpec(assignment);
        if (spec.Blocks.Count == 0) return Problem(400, "MATH_HAS_NO_BLOCKS", "tasks.math.start", "В math-задании пока нет блоков.");
        var unlimitedAttempts = spec.Settings.UnlimitedAttempts || HasUnlimitedAiTaskAttempts(http, cfg);
        var ignoreTimeLimit = IgnoreAiTaskAttemptTimeLimits(http, cfg);
        var active = await db.Attempts.FirstOrDefaultAsync(x => x.Kind == "math" && x.TaskAssignmentId == assignmentId && x.UserId == userId.Value && x.SubmittedAt == null);
        if (active != null) return Microsoft.AspNetCore.Http.Results.Ok(MathStartDto(active, spec, unlimitedAttempts, ignoreTimeLimit));
        var used = await db.Attempts.CountAsync(x => x.Kind == "math" && x.TaskAssignmentId == assignmentId && x.UserId == userId.Value);
        var max = unlimitedAttempts ? int.MaxValue : spec.Settings.MaxAttempts;
        if (used + 1 > max) return Microsoft.AspNetCore.Http.Results.Json(new { message = "Достигнут лимит попыток.", code = "ATTEMPT_LIMIT_REACHED" }, statusCode: StatusCodes.Status409Conflict);
        if (await ConsumeTaskEnergyAsync(http, cfg, clients, userId.Value, "math-attempt-start", ct) is { } quotaProblem) return quotaProblem;
        var attempt = new TaskAttempt { Kind = "math", TaskAssignmentId = assignmentId, UserId = userId.Value, AttemptNumber = used + 1, TimeLimitSeconds = ignoreTimeLimit ? null : TimeLimitFor(spec.Settings.AttemptTimeLimitsSeconds, used + 1) };
        attempt.OrderJson = JsonSerializer.Serialize(OrderedIds(spec.Blocks.Select(x => x.Id), spec.Settings.ShuffleBlocks, attempt.Id), JsonOptions());
        db.Attempts.Add(attempt);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch
        {
            await RefundTaskEnergyAsync(http, cfg, clients, userId.Value, "math-attempt-start-failed", CancellationToken.None);
            throw;
        }
        return Microsoft.AspNetCore.Http.Results.Ok(MathStartDto(attempt, spec, unlimitedAttempts, ignoreTimeLimit));
    }

    internal static async Task<IResult> SubmitMath(Guid assignmentId, JsonElement payload, HttpContext http, IConfiguration cfg, TasksDbContext db, IHttpClientFactory clients, CancellationToken ct)
    {
        var userId = RequireUser(http, cfg);
        if (userId == null) return Unauthorized();
        var attemptId = GuidProp(payload, "attemptId");
        if (attemptId == Guid.Empty) return Problem(400, "ATTEMPT_ID_REQUIRED", "tasks.math.submit", "Не передан attemptId.");
        var attempt = await db.Attempts.FirstOrDefaultAsync(x => x.Id == attemptId && x.Kind == "math" && x.TaskAssignmentId == assignmentId && x.UserId == userId.Value);
        if (attempt == null) return Microsoft.AspNetCore.Http.Results.NotFound(new { message = "Попытка не найдена.", code = "ATTEMPT_NOT_FOUND" });
        if (attempt.SubmittedAt != null) return Problem(400, "ATTEMPT_ALREADY_SUBMITTED", "tasks.math.submit", "Эта попытка уже была отправлена.");
        var assignment = await db.Assignments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == assignmentId, ct);
        if (assignment == null || !await CanUserAccessAssignmentAsync(assignment, http, cfg, db, clients, ct)) return Microsoft.AspNetCore.Http.Results.NotFound(new { message = "Задание не найдено.", code = "ASSIGNMENT_NOT_FOUND" });
        var spec = ReadMathSpec(assignment);
        var answers = MathAnswersArray(payload, "answers");
        var byAnswer = answers.GroupBy(x => x.BlockId).ToDictionary(x => x.Key, x => x.First());
        var order = ParseGuidList(attempt.OrderJson);
        if (order.Count == 0) order = spec.Blocks.Select(x => x.Id).ToList();
        var map = spec.Blocks.ToDictionary(x => x.Id);
        var totalScore = 0; var earned = 0; var correct = 0;
        var review = new JsonArray();
        foreach (var id in order)
        {
            if (!map.TryGetValue(id, out var b)) continue;
            if (b.Kind == "info") continue;
            totalScore += System.Math.Max(1, b.Score);
            byAnswer.TryGetValue(id, out var ans);
            var ok = IsMathCorrect(b, ans);
            if (ok) { correct++; earned += System.Math.Max(1, b.Score); }
            review.Add(MathBlockReviewNode(b, ans, ok));
        }
        var score = totalScore == 0 ? 0 : (int)System.Math.Floor(earned * 100.0 / totalScore);
        var ignoreTimeLimit = IgnoreAiTaskAttemptTimeLimits(http, cfg);
        var unlimitedAttempts = spec.Settings.UnlimitedAttempts || HasUnlimitedAiTaskAttempts(http, cfg);
        attempt.SubmittedAt = DateTimeOffset.UtcNow;
        attempt.TimeExpired = !ignoreTimeLimit && IsTimeExpired(attempt);
        attempt.TotalUnits = spec.Blocks.Count(x => x.Kind != "info"); attempt.CorrectUnits = correct; attempt.TotalScore = totalScore; attempt.EarnedScore = earned;
        attempt.ScorePercent = attempt.TimeExpired ? 0 : score;
        attempt.Passed = !attempt.TimeExpired && attempt.ScorePercent >= spec.Settings.PassPercent;
        attempt.AnswersJson = JsonSerializer.Serialize(answers, JsonOptions());
        attempt.ReviewJson = new JsonObject { ["blocks"] = review }.ToJsonString(JsonOptions());
        attempt.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        TaskForgeDebugTrace.Map("MATH_ATTEMPT_SAVED",
            ("user", userId.Value),
            ("assignment", assignmentId),
            ("attempt", attempt.Id),
            ("attemptNumber", attempt.AttemptNumber),
            ("scorePercent", attempt.ScorePercent),
            ("passed", attempt.Passed),
            ("timeExpired", attempt.TimeExpired),
            ("submittedAt", attempt.SubmittedAt));
        await MarkRatingDirtyInSolutionsAsync(clients, cfg, new[] { userId.Value }, "math-attempt-submitted", assignmentId, ct);
        return Microsoft.AspNetCore.Http.Results.Ok(new { attemptId = attempt.Id, attempt.AttemptNumber, maxAttempts = unlimitedAttempts ? 0 : spec.Settings.MaxAttempts, unlimitedAttempts, passPercent = spec.Settings.PassPercent, totalScore, earnedScore = earned, scorePercent = attempt.ScorePercent, attempt.TimeExpired, attempt.Passed });
    }

    internal static object MathStartDto(TaskAttempt attempt, MathSpec spec, bool unlimitedAttempts = false, bool ignoreTimeLimit = false)
    {
        var order = ParseGuidList(attempt.OrderJson);
        var map = spec.Blocks.ToDictionary(x => x.Id);
        var blocks = (order.Count == 0 ? spec.Blocks : order.Where(map.ContainsKey).Select(id => map[id])).Select(x => MathBlockPublicDto(x)).ToList();
        return new { attemptId = attempt.Id, attempt.AttemptNumber, maxAttempts = unlimitedAttempts ? 0 : spec.Settings.MaxAttempts, unlimitedAttempts, passPercent = spec.Settings.PassPercent, attemptTimeLimitSeconds = ignoreTimeLimit ? null : attempt.TimeLimitSeconds, startedAt = attempt.StartedAt, startedAtUtc = attempt.StartedAt, spec.Settings.ShuffleBlocks, blocks };
    }

    internal static MathSpec ReadMathSpec(Assignment assignment)
    {
        var root = JsonNode.Parse(string.IsNullOrWhiteSpace(assignment.TestsJson) ? "{}" : assignment.TestsJson!) as JsonObject ?? new JsonObject();
        return ParseMathSpec(root);
    }

    internal static bool IsMathCorrect(MathBlock b, MathAnswer? a)
    {
        var k = b.Kind.ToLowerInvariant();
        if (k is "single-choice" or "multi-choice") return SetEq(a?.SelectedOptionKeys ?? [], b.CorrectOptionKeys);
        if (k is "text" or "fill" or "formula" or "numeric") return TextAccepted(a?.Text, b.AcceptedAnswers, b.CaseSensitive, b.Trim, b.NumericTolerance);
        if (k is "order") return SeqEq(a?.OrderedItems ?? [], b.OrderItems);
        if (k is "match") return MatchEq(a?.MatchPairs ?? [], b.MatchPairs);
        return true;
    }

    internal static List<MathAnswer> MathAnswersArray(JsonElement payload, string name)
    {
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array) return [];
        return arr.EnumerateArray().Select(x => new MathAnswer(GuidProp(x, "blockId"), x.TryGetProperty("text", out var t) ? t.ToString() : null, Strings(x, "selectedOptionKeys"), Strings(x, "orderedItems"), MatchPairs(x, "matchPairs"))).ToList();
    }

    internal static MathSpec ParseMathSpec(JsonObject root)
    {
        var settings = root["settings"] as JsonObject ?? new JsonObject();
        var blocks = root["blocks"] as JsonArray ?? new JsonArray();
        return new MathSpec(
            new MathSettings(
                Int(settings, "maxAttempts", 1),
                Bool(settings, "unlimitedAttempts", false),
                System.Math.Clamp(Int(settings, "passPercent", 60), 0, 100),
                Bool(settings, "shuffleBlocks", false),
                Bool(settings, "allowReview", true),
                IntList(settings, "attemptTimeLimitsSeconds")),
            blocks.OfType<JsonObject>().Select(ParseBlock).OrderBy(x => x.Order).ToList());
    }

    internal static JsonObject MathSpecToJsonObject(MathSpec spec) => new()
    {
        ["settings"] = JsonSerializer.SerializeToNode(spec.Settings, JsonOptions()),
        ["blocks"] = JsonSerializer.SerializeToNode(spec.Blocks, JsonOptions())
    };

    internal static object MathBlockPublicDto(MathBlock block) => new
    {
        block.Id,
        block.Order,
        block.Kind,
        block.Prompt,
        block.PromptContentJson,
        block.Score,
        block.IsRequired,
        options = block.Options,
        orderItems = block.OrderItems,
        matchLeftItems = block.MatchLeftItems,
        matchRightItems = block.MatchRightItems
    };

    internal static JsonObject MathBlockReviewNode(MathBlock block, MathAnswer? answer, bool isCorrect) => new()
    {
        ["id"] = block.Id.ToString(),
        ["order"] = block.Order,
        ["kind"] = block.Kind,
        ["prompt"] = block.Prompt,
        ["promptContentJson"] = block.PromptContentJson,
        ["score"] = block.Score,
        ["isRequired"] = block.IsRequired,
        ["options"] = JsonSerializer.SerializeToNode(block.Options, JsonOptions()),
        ["correctOptionKeys"] = JsonSerializer.SerializeToNode(block.CorrectOptionKeys, JsonOptions()),
        ["acceptedAnswers"] = JsonSerializer.SerializeToNode(block.AcceptedAnswers, JsonOptions()),
        ["orderItems"] = JsonSerializer.SerializeToNode(block.OrderItems, JsonOptions()),
        ["matchPairs"] = JsonSerializer.SerializeToNode(block.MatchPairs, JsonOptions()),
        ["userAnswer"] = JsonSerializer.SerializeToNode(answer, JsonOptions()),
        ["isCorrect"] = isCorrect
    };

}
