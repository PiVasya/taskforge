using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using TaskForge.Tasks.Api.Data;
using TaskForge.Tasks.Api.Domain;
using TaskForge.Tasks.Api.Services.Ownership;
using TaskForge.Tasks.Api.Contracts;
using TaskForge.Tasks.Api.Services.Specs;
using TaskForge.Tasks.Api.Services.Testing;
using static TaskForge.Tasks.Api.Services.Common.AssignmentApiCommonService;
using static TaskForge.Tasks.Api.Services.Math.AssignmentApiMathService;
using static TaskForge.Tasks.Api.Services.Serialization.AssignmentApiSerializationService;
using static TaskForge.Tasks.Api.Services.Testing.AssignmentApiTestingService;

namespace TaskForge.Tasks.Api.Services.Results;

internal static class AssignmentApiResultsService
{
    private static DateTimeOffset? Since(int? days) =>
        days.HasValue && days.Value > 0 ? DateTimeOffset.UtcNow.AddDays(-days.Value) : null;

    private static async Task<Guid[]?> CourseAssignments(Guid? courseId, TasksDbContext db, CancellationToken ct)
    {
        if (!courseId.HasValue) return null;
        return await db.Assignments.AsNoTracking().Where(x => x.CourseId == courseId.Value)
            .Select(x => x.Id).ToArrayAsync(ct);
    }

    private static async Task<List<TaskAttempt>> SubmittedAsync(string kind, Guid? userId,
        Guid? courseId, Guid? assignmentId, int? days, int skip, int take,
        TasksDbContext db, IHttpClientFactory clients, IConfiguration cfg, CancellationToken ct)
    {
        var allowed = await CourseAssignments(courseId, db, ct);
        if (allowed is { Length: 0 }) return [];
        var from = Since(days);
        var query = new InteractiveAttemptClient.AttemptSearch(kind, userId, assignmentId,
            Since: from, SubmittedOnly: true, Take: global::System.Math.Clamp(take, 1, 2000), Skip: global::System.Math.Max(skip, 0));
        if (allowed is null) return (await InteractiveAttemptClient.SearchAsync(clients, cfg, query, ct)).Rows.ToList();
        var rows = new List<TaskAttempt>();
        foreach (var chunk in allowed.Chunk(2000))
            rows.AddRange(await InteractiveAttemptClient.LoadAllAsync(clients, cfg,
                query with { AssignmentIds = chunk, Skip = 0 }, ct));
        return rows.OrderByDescending(x => x.SubmittedAt).ThenByDescending(x => x.Id)
            .Skip(global::System.Math.Max(0, skip)).Take(global::System.Math.Clamp(take, 1, 200)).ToList();
    }

    internal static async Task<int> CountAttempts(string kind, Guid? userId, Guid? courseId,
        Guid? assignmentId, int? days, TasksDbContext db, IHttpClientFactory clients, IConfiguration cfg,
        CancellationToken ct = default)
    {
        var allowed = await CourseAssignments(courseId, db, ct);
        if (allowed is { Length: 0 }) return 0;
        var query = new InteractiveAttemptClient.AttemptSearch(kind, userId, assignmentId,
            Since: Since(days), SubmittedOnly: true, Take: 1);
        if (allowed is null) return checked((int)(await InteractiveAttemptClient.SearchAsync(clients, cfg, query, ct)).Total);
        long total = 0;
        foreach (var chunk in allowed.Chunk(2000))
            total += (await InteractiveAttemptClient.SearchAsync(clients, cfg, query with { AssignmentIds = chunk }, ct)).Total;
        return checked((int)total);
    }

    internal static async Task<List<object>> ListAttempts(string kind, Guid? userId, Guid? courseId,
        Guid? assignmentId, int? days, int skip, int take, TasksDbContext db,
        IHttpClientFactory clients, IConfiguration cfg, CancellationToken ct = default)
    {
        var rows = await SubmittedAsync(kind, userId, courseId, assignmentId, days,
            skip, global::System.Math.Clamp(take <= 0 ? 50 : take, 1, 200), db, clients, cfg, ct);
        var assignmentIds = rows.Select(x => x.TaskAssignmentId).Distinct().ToArray();
        var map = await db.Assignments.AsNoTracking().Where(x => assignmentIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
        return rows.Select(x =>
        {
            map.TryGetValue(x.TaskAssignmentId, out var a);
            return (object)new { attemptId = x.Id, userId = x.UserId, taskAssignmentId = x.TaskAssignmentId,
                courseId = a?.CourseId ?? Guid.Empty, courseTitle = "", assignmentTitle = a?.Title ?? "Задание",
                x.AttemptNumber, submittedAt = x.SubmittedAt, x.ScorePercent, x.Passed, x.TimeExpired, allowReview = true };
        }).ToList();
    }

    internal static bool CanReadAttemptReview(TaskAttempt attempt, Guid? userId, bool isAdmin) =>
        isAdmin || (userId.HasValue && attempt.UserId == userId.Value);

    internal static async Task<IResult> ReviewAttempt(Guid attemptId, string kind, Guid? userId,
        bool isAdmin, TasksDbContext db, IHttpClientFactory clients, IConfiguration cfg, CancellationToken ct = default)
    {
        var attempt = await InteractiveAttemptClient.FindAsync(clients, cfg, attemptId, ct);
        if (attempt == null || attempt.Kind != kind)
            return global::Microsoft.AspNetCore.Http.Results.NotFound(new { message = "Попытка не найдена.", code = "ATTEMPT_NOT_FOUND" });
        if (!CanReadAttemptReview(attempt, userId, isAdmin))
            return global::Microsoft.AspNetCore.Http.Results.Json(new { message = "Нет доступа к этой попытке.", code = "ATTEMPT_FORBIDDEN" }, statusCode: 403);
        var assignment = await db.Assignments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == attempt.TaskAssignmentId, ct);
        var review = ParseJson(attempt.ReviewJson);
        if (kind == "math")
        {
            var spec = assignment == null ? null : await AssignmentTypeSpecService.ReadMathAsync(db, assignment, ct);
            var allowReview = isAdmin || spec?.Settings.AllowReview == true;
            return global::Microsoft.AspNetCore.Http.Results.Ok(new { attemptId = attempt.Id, attempt.TaskAssignmentId,
                courseId = assignment?.CourseId ?? Guid.Empty, courseTitle = "", assignmentTitle = assignment?.Title ?? "Задание",
                attempt.UserId, attempt.AttemptNumber, attempt.StartedAt, submittedAt = attempt.SubmittedAt,
                passPercent = spec?.Settings.PassPercent ?? 60, attempt.TotalScore, attempt.EarnedScore,
                attempt.ScorePercent, attempt.Passed, attempt.TimeExpired, allowReview,
                blocks = allowReview ? JsonPropArray(review, "blocks") : Array.Empty<object>() });
        }
        var testSpec = assignment == null ? null : await TestAssignmentSpecService.ReadAsync(db, assignment, ct);
        var testAllowReview = isAdmin || testSpec?.Settings.AllowReview == true;
        return global::Microsoft.AspNetCore.Http.Results.Ok(new { attemptId = attempt.Id, attempt.TaskAssignmentId,
            courseId = assignment?.CourseId ?? Guid.Empty, courseTitle = "", assignmentTitle = assignment?.Title ?? "Задание",
            attempt.UserId, attempt.AttemptNumber, attempt.StartedAt, submittedAt = attempt.SubmittedAt,
            passPercent = testSpec?.Settings.PassPercent ?? 60, totalQuestions = attempt.TotalUnits,
            correctQuestions = attempt.CorrectUnits, attempt.ScorePercent, attempt.Passed,
            attempt.TimeExpired, allowReview = testAllowReview, questionsReflectCurrentVersion = true,
            questions = testAllowReview && testSpec is not null ? CurrentTestReview(attempt, testSpec) : new JsonArray() });
    }

    // Pure filter retained for test fixtures and consumers that already have a materialized sample.
    internal static IQueryable<TaskAttempt> SubmittedAttemptsForUser(IQueryable<TaskAttempt> attempts, Guid userId, string kind) =>
        attempts.Where(x => x.UserId == userId && x.Kind == kind && x.SubmittedAt != null);

    internal static async Task<IResult> DeleteUserAttempts(Guid userId, string kind, TasksDbContext db,
        IHttpClientFactory clients, IConfiguration cfg, CancellationToken ct)
    {
        if (userId == Guid.Empty) return global::Microsoft.AspNetCore.Http.Results.BadRequest(new { code = "USER_ID_REQUIRED" });
        var deleted = await InteractiveAttemptClient.DeleteUserAsync(clients, cfg, userId, kind, ct);
        return global::Microsoft.AspNetCore.Http.Results.Ok(new { deleted = deleted.Deleted, userId, kind });
    }

    internal static async Task<IResult> DeleteAttempt(Guid attemptId, string kind, TasksDbContext db,
        IHttpClientFactory clients, IConfiguration cfg, CancellationToken ct)
    {
        var item = await InteractiveAttemptClient.FindAsync(clients, cfg, attemptId, ct);
        if (item is null || item.Kind != kind)
            return global::Microsoft.AspNetCore.Http.Results.NotFound(new { message = "Попытка не найдена.", code = "ATTEMPT_NOT_FOUND" });
        await InteractiveAttemptClient.DeleteAsync(clients, cfg, attemptId, ct);
        return global::Microsoft.AspNetCore.Http.Results.NoContent();
    }

    internal static async Task<HashSet<Guid>> LoadSolvedAssignmentIdsAsync(Guid userId,
        IEnumerable<Guid> assignmentIds, TasksDbContext db, IHttpClientFactory clients, IConfiguration cfg, CancellationToken ct)
    {
        var ids = assignmentIds.Where(x => x != Guid.Empty).Distinct().ToArray();
        var solved = new HashSet<Guid>();
        foreach (var batch in ids.Chunk(2000))
        {
            var url = (cfg["Services:SolutionsApi"] ?? cfg["ServiceUrls:SolutionsApi"] ?? "http://solutions-api:8080").TrimEnd('/');
            var key = cfg["InternalApi:Key"] ?? cfg["TaskForgeInternalApi:ApiKey"] ?? cfg["TaskForge:InternalKey"] ??
                Environment.GetEnvironmentVariable("TASKFORGE_INTERNAL_KEY");
            if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("Internal key is required for solved-progress lookup.");
            using var message = new HttpRequestMessage(HttpMethod.Post, $"{url}/api/internal/users/{userId:D}/solved-assignments")
            {
                Content = JsonContent.Create(new { assignmentIds = batch })
            };
            message.Headers.TryAddWithoutValidation("X-Internal-Key", key);
            using var response = await clients.CreateClient("interactive-attempt-owner").SendAsync(message, ct);
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<SolvedAssignmentsResponse>(cancellationToken: ct)
                ?? throw new HttpRequestException("Solutions returned an empty solved-progress response.");
            foreach (var id in result.SolvedAssignmentIds ?? [])
                if (id != Guid.Empty) solved.Add(id);
        }
        return solved;
    }
}
