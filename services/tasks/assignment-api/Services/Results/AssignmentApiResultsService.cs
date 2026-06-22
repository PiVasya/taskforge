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
using static TaskForge.Tasks.Api.Services.Serialization.AssignmentApiSerializationService;
using static TaskForge.Tasks.Api.Services.Testing.AssignmentApiTestingService;

namespace TaskForge.Tasks.Api.Services.Results;

internal static class AssignmentApiResultsService
{
    internal static async Task<List<object>> ListAttempts(string kind, Guid? userId, Guid? courseId, Guid? assignmentId, int? days, int skip, int take, TasksDbContext db)
    {
        var q = db.Attempts.AsNoTracking().Where(x => x.Kind == kind && x.SubmittedAt != null);
        if (userId.HasValue) q = q.Where(x => x.UserId == userId.Value);
        if (assignmentId.HasValue) q = q.Where(x => x.TaskAssignmentId == assignmentId.Value);
        if (days.HasValue && days.Value > 0) q = q.Where(x => x.SubmittedAt >= DateTimeOffset.UtcNow.AddDays(-days.Value));
        var rows = await q.OrderByDescending(x => x.SubmittedAt).Skip(System.Math.Max(0, skip)).Take(System.Math.Clamp(take <= 0 ? 50 : take, 1, 200)).ToListAsync();
        var assignmentIds = rows.Select(x => x.TaskAssignmentId).Distinct().ToList();
        var map = await db.Assignments.AsNoTracking().Where(x => assignmentIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id);
        if (courseId.HasValue) rows = rows.Where(x => map.TryGetValue(x.TaskAssignmentId, out var a) && a.CourseId == courseId.Value).ToList();
        return rows.Select(x =>
        {
            map.TryGetValue(x.TaskAssignmentId, out var a);
            return (object)new { attemptId = x.Id, taskAssignmentId = x.TaskAssignmentId, courseId = a?.CourseId ?? Guid.Empty, courseTitle = "", assignmentTitle = a?.Title ?? "Задание", x.AttemptNumber, submittedAt = x.SubmittedAt, x.ScorePercent, x.Passed, x.TimeExpired, allowReview = true };
        }).ToList();
    }

    internal static async Task<IResult> ReviewAttempt(Guid attemptId, string kind, Guid? userId, bool isAdmin, TasksDbContext db)
    {
        var attempt = await db.Attempts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == attemptId && x.Kind == kind);
        if (attempt == null) return Microsoft.AspNetCore.Http.Results.NotFound(new { message = "Попытка не найдена.", code = "ATTEMPT_NOT_FOUND" });
        if (!isAdmin && userId.HasValue && attempt.UserId != userId.Value) return Microsoft.AspNetCore.Http.Results.Json(new { message = "Нет доступа к этой попытке.", code = "ATTEMPT_FORBIDDEN" }, statusCode: StatusCodes.Status403Forbidden);
        var assignment = await db.Assignments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == attempt.TaskAssignmentId);
        var review = ParseJson(attempt.ReviewJson);

        if (kind == "math")
        {
            var spec = assignment == null ? null : ReadMathSpec(assignment);
            var allowReview = isAdmin || spec?.Settings.AllowReview == true;
            return Microsoft.AspNetCore.Http.Results.Ok(new { attemptId = attempt.Id, attempt.TaskAssignmentId, courseId = assignment?.CourseId ?? Guid.Empty, courseTitle = "", assignmentTitle = assignment?.Title ?? "Задание", attempt.UserId, attempt.AttemptNumber, attempt.StartedAt, submittedAt = attempt.SubmittedAt, passPercent = spec?.Settings.PassPercent ?? 60, attempt.TotalScore, attempt.EarnedScore, attempt.ScorePercent, attempt.Passed, attempt.TimeExpired, allowReview, blocks = allowReview ? JsonPropArray(review, "blocks") : Array.Empty<object>() });
        }

        var testSpec = assignment == null ? null : ReadTaskSpec(assignment);
        var testAllowReview = isAdmin || testSpec?.Settings.AllowReview == true;
        return Microsoft.AspNetCore.Http.Results.Ok(new { attemptId = attempt.Id, attempt.TaskAssignmentId, courseId = assignment?.CourseId ?? Guid.Empty, courseTitle = "", assignmentTitle = assignment?.Title ?? "Задание", attempt.UserId, attempt.AttemptNumber, attempt.StartedAt, submittedAt = attempt.SubmittedAt, passPercent = testSpec?.Settings.PassPercent ?? 60, totalQuestions = attempt.TotalUnits, correctQuestions = attempt.CorrectUnits, attempt.ScorePercent, attempt.Passed, attempt.TimeExpired, allowReview = testAllowReview, questions = testAllowReview ? JsonPropArray(review, "questions") : Array.Empty<object>() });
    }

    internal static async Task<IResult> DeleteAttempt(Guid attemptId, string kind, TasksDbContext db, IHttpClientFactory clients, IConfiguration cfg, CancellationToken ct)
    {
        var attempt = await db.Attempts.FirstOrDefaultAsync(x => x.Id == attemptId && x.Kind == kind, ct);
        if (attempt == null) return Microsoft.AspNetCore.Http.Results.NotFound(new { message = "Попытка не найдена.", code = "ATTEMPT_NOT_FOUND" });
        var userId = attempt.UserId;
        var assignmentId = attempt.TaskAssignmentId;
        db.Attempts.Remove(attempt);
        await db.SaveChangesAsync(ct);
        await MarkRatingDirtyInSolutionsAsync(clients, cfg, new[] { userId }, $"{kind}-attempt-deleted", assignmentId, ct);
        return Microsoft.AspNetCore.Http.Results.NoContent();
    }

    internal static async Task<HashSet<Guid>> LoadSolvedAssignmentIdsAsync(Guid userId, IEnumerable<Guid> assignmentIds, TasksDbContext db, IHttpClientFactory clients, IConfiguration cfg, CancellationToken ct)
    {
        var ids = assignmentIds.Where(x => x != Guid.Empty).Distinct().Take(2000).ToArray();
        var solved = new HashSet<Guid>();
        if (ids.Length == 0) return solved;

        var taskSolved = await db.Attempts.AsNoTracking()
            .Where(x => x.UserId == userId && ids.Contains(x.TaskAssignmentId) && x.Passed)
            .Select(x => x.TaskAssignmentId)
            .Distinct()
            .ToListAsync(ct);
        foreach (var id in taskSolved) solved.Add(id);

        var response = await PostInternalAsync<SolvedAssignmentsResponse>(
            clients,
            cfg,
            ServiceUrl(cfg, "SolutionsApi", "http://solutions-api:8080"),
            $"/api/internal/users/{userId:D}/solved-assignments",
            new SolvedAssignmentsRequest(ids),
            ct);

        if (response?.SolvedAssignmentIds != null)
        {
            foreach (var id in response.SolvedAssignmentIds)
            {
                if (id != Guid.Empty) solved.Add(id);
            }
        }

        return solved;
    }

}
