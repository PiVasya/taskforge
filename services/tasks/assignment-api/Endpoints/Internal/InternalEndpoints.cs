using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using TaskForge.Tasks.Api.Data;
using TaskForge.Tasks.Api.Domain;
using TaskForge.Tasks.Api.Services.Access;

using TaskForge.Tasks.Api.Contracts;
using static TaskForge.Tasks.Api.Services.Access.AssignmentApiAccessService;
using static TaskForge.Tasks.Api.Services.Common.AssignmentApiCommonService;
using static TaskForge.Tasks.Api.Services.Image.AssignmentApiImageService;
using static TaskForge.Tasks.Api.Services.Mapping.AssignmentApiMappingService;
using static TaskForge.Tasks.Api.Services.Math.AssignmentApiMathService;
using static TaskForge.Tasks.Api.Services.Results.AssignmentApiResultsService;
using static TaskForge.Tasks.Api.Services.Serialization.AssignmentApiSerializationService;
using static TaskForge.Tasks.Api.Services.Testing.AssignmentApiTestingService;

namespace TaskForge.Tasks.Api.Endpoints;

internal static partial class AssignmentApiEndpoints
{
    private static WebApplication MapInternalEndpoints(WebApplication app)
    {
        app.MapGet("/api/internal/assignments/{assignmentId:guid}/judge-spec", async (Guid assignmentId, Guid? engineProfileId, TasksDbContext db, CancellationToken ct) =>
        {
            var assignment = await db.Assignments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == assignmentId);
            if (assignment == null)
            {
                return Microsoft.AspNetCore.Http.Results.NotFound(new { message = "Задание не найдено.", code = "ASSIGNMENT_NOT_FOUND" });
            }

            if (assignment.Type == "sql-test")
            {
                var root = await db.SqlAssignmentSpecs.AsNoTracking().FirstOrDefaultAsync(x => x.AssignmentId == assignmentId, ct);
                if (root?.PublishedVersionId is null || !engineProfileId.HasValue)
                    return Microsoft.AspNetCore.Http.Results.Conflict(new { code = "SQL_NOT_VALIDATED", message = "A published SQL revision and engine profile are required." });
                try
                {
                    var payload = await TaskForge.Tasks.Api.Services.Sql.SqlTaskService.Payload(db, root.PublishedVersionId.Value, engineProfileId.Value, true, ct);
                    return Microsoft.AspNetCore.Http.Results.Ok(new { assignment.Id, assignment.Type, sql = payload });
                }
                catch (TaskForge.Tasks.Api.Services.Sql.SqlNotFoundException) { return Microsoft.AspNetCore.Http.Results.NotFound(); }
                catch (TaskForge.Tasks.Api.Services.Sql.SqlNotReadyException) { return Microsoft.AspNetCore.Http.Results.Conflict(new { code = "SQL_NOT_VALIDATED" }); }
            }

            return Microsoft.AspNetCore.Http.Results.Ok(new
            {
                assignment.Id,
                assignment.Type,
                assignment.Language,
                allowedLanguages = ParseCsv(assignment.AllowedLanguagesCsv, assignment.Language),
                codeForbiddenCalls = ParseStringArrayJson(assignment.CodeForbiddenCallsJson),
                codeRequiredCalls = ParseStringArrayJson(assignment.CodeRequiredCallsJson),
                tests = ParseJson(assignment.TestsJson),
                testCases = ParseJson(assignment.TestsJson),
                testsJson = assignment.TestsJson
            });
        });

        app.MapGet("/api/internal/assignments/{assignmentId:guid}/access/{userId:guid}", async (Guid assignmentId, Guid userId, TasksDbContext db, IHttpClientFactory clients, IConfiguration cfg, CancellationToken ct) =>
        {
            var assignment = await db.Assignments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == assignmentId, ct);
            if (assignment == null) return Microsoft.AspNetCore.Http.Results.NotFound(new { message = "Задание не найдено.", code = "ASSIGNMENT_NOT_FOUND" });

            var courseAccess = await LoadCourseAccessAsync(assignment.CourseId, userId, clients, cfg, ct);
            var evaluation = assignment.IsVisible && courseAccess?.CanView == true
                ? await CourseMapProgressionService.LoadEvaluationAsync(assignment.CourseId, userId, db, clients, cfg, ct)
                : null;
            var canView = assignment.IsVisible && evaluation?.VisibleAssignmentIds.Contains(assignment.Id) == true;
            return Microsoft.AspNetCore.Http.Results.Ok(new
            {
                assignmentId,
                assignment.CourseId,
                userId,
                canView,
                canSubmit = canView,
                assignment.IsVisible,
                canEdit = courseAccess?.CanEdit == true
            });
        });

        app.MapPost("/api/internal/assignments/summaries", async (AssignmentIdsRequest request, TasksDbContext db, IDistributedCache cache, IConfiguration cfg, ILogger<Program> logger, CancellationToken ct) =>
        {
            var ids = (request.AssignmentIds ?? Array.Empty<Guid>()).Where(x => x != Guid.Empty).Distinct().Take(2000).OrderBy(x => x).ToArray();
            if (ids.Length == 0) return Microsoft.AspNetCore.Http.Results.Ok(Array.Empty<AssignmentSummaryDto>());

            // Assignment metadata is used for rating calculations. Do not cache it here:
            // an admin can change assignment.rating and the user's displayed rating must update immediately.
            var assignments = await db.Assignments.AsNoTracking().Where(x => ids.Contains(x.Id)).ToListAsync(ct);
            return Microsoft.AspNetCore.Http.Results.Ok(assignments.Select(ToAssignmentSummaryDto).ToList());
        });

        app.MapGet("/api/internal/users/{userId:guid}/activity-summary", async (Guid userId, TasksDbContext db, CancellationToken ct) =>
        {
            var testAttempts = await db.Attempts.AsNoTracking().Where(x => x.UserId == userId && x.Kind == "test").ToListAsync(ct);
            var mathAttempts = await db.Attempts.AsNoTracking().Where(x => x.UserId == userId && x.Kind == "math").ToListAsync(ct);
            var solvedIds = testAttempts.Where(x => x.Passed).Select(x => x.TaskAssignmentId)
                .Concat(mathAttempts.Where(x => x.Passed).Select(x => x.TaskAssignmentId))
                .Where(x => x != Guid.Empty)
                .Distinct()
                .ToArray();
            var ratings = solvedIds.Length == 0
                ? new List<int>()
                : await db.Assignments.AsNoTracking()
                    .Where(x => solvedIds.Contains(x.Id))
                    .Select(x => x.Rating)
                    .ToListAsync(ct);
            return Microsoft.AspNetCore.Http.Results.Ok(new
            {
                solvedAssignments = solvedIds.Length,
                totalAttempts = testAttempts.Count + mathAttempts.Count,
                codeSolutions = 0,
                imageSolutions = 0,
                testAttempts = testAttempts.Count,
                mathAttempts = mathAttempts.Count,
                score = ratings.Sum(x => System.Math.Max(1, x)),
                rating = ratings.Sum(x => System.Math.Max(1, x))
            });
        });

        app.MapGet("/api/internal/assignments/analytics/summary", async (DateTimeOffset? fromUtc, DateTimeOffset? toUtc, int days, TasksDbContext db, CancellationToken ct) =>
        {
            static double Percent(int num, int den) => den <= 0 ? 0 : System.Math.Round(num * 100.0 / den, 1);

            days = System.Math.Clamp(days <= 0 ? 30 : days, 1, 365);
            var to = toUtc ?? DateTimeOffset.UtcNow;
            var from = fromUtc ?? to.AddDays(-days);

            var attempts = await db.Attempts.AsNoTracking()
                .Where(x => x.SubmittedAt.HasValue && x.SubmittedAt.Value >= from && x.SubmittedAt.Value <= to)
                .Join(db.Assignments.AsNoTracking(), attempt => attempt.TaskAssignmentId, assignment => assignment.Id, (attempt, assignment) => new AssignmentAttemptAnalyticsRow
                {
                    AssignmentId = attempt.TaskAssignmentId,
                    CourseId = assignment.CourseId,
                    UserId = attempt.UserId,
                    Kind = attempt.Kind,
                    Language = assignment.Language,
                    Title = assignment.Title,
                    Type = assignment.Type,
                    Difficulty = assignment.Difficulty,
                    Rating = assignment.Rating,
                    Passed = attempt.Passed,
                    ScorePercent = attempt.ScorePercent,
                    CreatedAt = attempt.SubmittedAt ?? attempt.CreatedAt,
                })
                .ToListAsync(ct);

            List<object> DayPoints(IEnumerable<AssignmentAttemptAnalyticsRow> rows, Func<IEnumerable<AssignmentAttemptAnalyticsRow>, double> selector)
            {
                var byDay = rows.GroupBy(x => x.CreatedAt.UtcDateTime.Date).ToDictionary(x => x.Key, x => x.AsEnumerable());
                var start = DateTime.UtcNow.Date.AddDays(-(days - 1));
                return Enumerable.Range(0, days).Select(i =>
                {
                    var day = start.AddDays(i);
                    var value = byDay.TryGetValue(day, out var vals) ? selector(vals) : 0;
                    return (object)new { label = day.ToString("dd.MM"), date = day.ToString("yyyy-MM-dd"), value = System.Math.Round(value, 1), count = System.Math.Round(value, 1) };
                }).ToList();
            }

            var assignmentRows = attempts.GroupBy(x => x.AssignmentId).Select(g =>
            {
                var first = g.First();
                var total = g.Count();
                var passed = g.Count(x => x.Passed);
                var uniqueUsers = g.Select(x => x.UserId).Distinct().Count();
                var successUsers = g.Where(x => x.Passed).Select(x => x.UserId).Distinct().Count();
                return new
                {
                    assignmentId = g.Key,
                    title = string.IsNullOrWhiteSpace(first.Title) ? "Задание без названия" : first.Title,
                    courseId = first.CourseId,
                    type = first.Kind,
                    difficulty = first.Difficulty,
                    rating = first.Rating,
                    attempts = total,
                    passed,
                    failed = total - passed,
                    uniqueUsers,
                    stuckUsers = System.Math.Max(0, uniqueUsers - successUsers),
                    successRate = Percent(passed, total),
                    value = total,
                };
            }).ToList();

            var totalAttempts = attempts.Count;
            var passedAttempts = attempts.Count(x => x.Passed);
            var scoredAttempts = attempts.Where(x => x.Kind == "test" || x.Kind == "math").ToList();

            return Microsoft.AspNetCore.Http.Results.Ok(new
            {
                totals = new
                {
                    totalAttempts,
                    passedAttempts,
                    failedAttempts = totalAttempts - passedAttempts,
                    successRate = Percent(passedAttempts, totalAttempts),
                    codeAttempts = 0,
                    testAttempts = attempts.Count(x => x.Kind == "test"),
                    imageAttempts = 0,
                    mathAttempts = attempts.Count(x => x.Kind == "math"),
                    avgTestScore = scoredAttempts.Count == 0 ? 0 : System.Math.Round(scoredAttempts.Average(x => x.ScorePercent), 1),
                },
                attemptsByDay = DayPoints(attempts, g => g.Count()),
                successByDay = DayPoints(attempts.Where(x => x.Passed), g => g.Count()),
                failureByDay = DayPoints(attempts.Where(x => !x.Passed), g => g.Count()),
                types = attempts.GroupBy(x => x.Kind).Select(g => new { label = g.Key, value = g.Count() }).OrderByDescending(x => x.value).ToList(),
                languages = attempts.Where(x => !string.IsNullOrWhiteSpace(x.Language)).GroupBy(x => x.Language).Select(g => new { label = g.Key, value = g.Count() }).OrderByDescending(x => x.value).Take(12).ToList(),
                topAssignments = assignmentRows.OrderByDescending(x => x.attempts).Take(20).ToList(),
                hardAssignments = assignmentRows.Where(x => x.attempts >= 2).OrderBy(x => x.successRate).ThenByDescending(x => x.failed).ThenByDescending(x => x.attempts).Take(20).ToList(),
            });
        });

        app.MapPost("/api/internal/activity/leaderboard", async (ActivityLeaderboardRequest request, TasksDbContext db, CancellationToken ct) =>
        {
            var since = request.Days.HasValue && request.Days.Value > 0 ? DateTimeOffset.UtcNow.AddDays(-request.Days.Value) : (DateTimeOffset?)null;
            var userFilter = (request.UserIds ?? Array.Empty<Guid>()).Where(x => x != Guid.Empty).Distinct().ToHashSet();
            var courseFilter = (request.CourseIds ?? Array.Empty<Guid>()).Where(x => x != Guid.Empty).Distinct().ToHashSet();
            var q = db.Attempts.AsNoTracking().Where(x => x.Passed);
            if (since.HasValue) q = q.Where(x => x.SubmittedAt.HasValue && x.SubmittedAt.Value >= since.Value);
            if (userFilter.Count > 0) q = q.Where(x => userFilter.Contains(x.UserId));

            var joined = await q.Join(db.Assignments.AsNoTracking(), a => a.TaskAssignmentId, assignment => assignment.Id, (a, assignment) => new { Attempt = a, Assignment = assignment })
                .Where(x => courseFilter.Count > 0
                    ? courseFilter.Contains(x.Assignment.CourseId)
                    : (!request.CourseId.HasValue || x.Assignment.CourseId == request.CourseId.Value))
                .Select(x => new
                {
                    userId = x.Attempt.UserId,
                    assignmentId = x.Attempt.TaskAssignmentId,
                    rating = x.Assignment.Rating,
                    submittedAt = x.Attempt.SubmittedAt ?? x.Attempt.CreatedAt,
                    kind = x.Attempt.Kind
                })
                .ToListAsync(ct);
            return Microsoft.AspNetCore.Http.Results.Ok(joined);
        });

        return app;
    }

    private sealed class AssignmentAttemptAnalyticsRow
    {
        public Guid AssignmentId { get; set; }
        public Guid CourseId { get; set; }
        public Guid UserId { get; set; }
        public string Kind { get; set; } = "test";
        public string Language { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public int Difficulty { get; set; }
        public int Rating { get; set; }
        public bool Passed { get; set; }
        public int ScorePercent { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
    }

}
