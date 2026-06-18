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
using static TaskForge.Tasks.Api.Services.Testing.AssignmentApiTestingService;

namespace TaskForge.Tasks.Api.Endpoints;

internal static partial class AssignmentApiEndpoints
{
    private static WebApplication MapInsightsEndpoints(WebApplication app)
    {
        app.MapGet("/api/admin/assignments/{assignmentId:guid}/insights", async (Guid assignmentId, TasksDbContext db, IConfiguration cfg, IHttpClientFactory httpFactory, CancellationToken ct) =>
        {
            var assignment = await db.Assignments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == assignmentId, ct);
            if (assignment == null) return Microsoft.AspNetCore.Http.Results.NotFound(new { message = "Задание не найдено.", code = "ASSIGNMENT_NOT_FOUND" });

            var rows = await db.Attempts.AsNoTracking().Where(x => x.TaskAssignmentId == assignmentId && x.SubmittedAt != null).OrderByDescending(x => x.SubmittedAt).ToListAsync(ct);
            var users = await LoadUserSummariesAsync(rows.Select(x => x.UserId).Distinct(), cfg, httpFactory, ct);
            var testRows = rows.Where(x => string.Equals(x.Kind, "test", StringComparison.OrdinalIgnoreCase)).ToList();
            var mathRows = rows.Where(x => string.Equals(x.Kind, "math", StringComparison.OrdinalIgnoreCase)).ToList();
            var uniqueUsers = rows.Select(x => x.UserId).Distinct().Count();
            var successUsers = rows.Where(x => x.Passed).Select(x => x.UserId).Distinct().Count();
            var avgScore = rows.Count == 0 ? 0 : System.Math.Round(rows.Average(x => x.ScorePercent), 1);
            var courseTitle = await LoadCourseTitleAsync(assignment.CourseId, cfg, httpFactory, ct);

            var solvers = rows.GroupBy(x => x.UserId).Select(g =>
            {
                var user = users.GetValueOrDefault(g.Key);
                return new
                {
                    userId = g.Key,
                    login = user?.Login,
                    fullName = UserLabel(user),
                    displayName = UserLabel(user),
                    email = user?.Email ?? user?.MaskedEmail,
                    attempts = g.Count(),
                    passed = g.Count(x => x.Passed),
                    successRate = Percent(g.Count(x => x.Passed), g.Count()),
                    bestScore = g.Max(x => x.ScorePercent),
                    lastActivityAtUtc = g.Max(x => x.SubmittedAt ?? x.CreatedAt)
                };
            }).OrderByDescending(x => x.passed).ThenByDescending(x => x.bestScore).ThenByDescending(x => x.lastActivityAtUtc).Take(50).ToList();

            var recent = rows.Take(100).Select(x =>
            {
                var user = users.GetValueOrDefault(x.UserId);
                var created = x.SubmittedAt ?? x.CreatedAt;
                return new
                {
                    attemptId = x.Id,
                    userId = x.UserId,
                    login = user?.Login,
                    fullName = UserLabel(user),
                    displayName = UserLabel(user),
                    email = user?.Email ?? user?.MaskedEmail,
                    sourceKind = x.Kind,
                    kind = x.Kind,
                    status = x.Passed ? "passed" : "failed",
                    passed = x.Passed,
                    scorePercent = x.ScorePercent,
                    durationSeconds = System.Math.Max(0, (int)System.Math.Round(((x.SubmittedAt ?? x.UpdatedAt) - x.StartedAt).TotalSeconds)),
                    createdAtUtc = created,
                    submittedAtUtc = x.SubmittedAt
                };
            }).ToList();

            return Microsoft.AspNetCore.Http.Results.Ok(new
            {
                assignmentId,
                title = assignment.Title,
                assignmentTitle = assignment.Title,
                courseId = assignment.CourseId,
                courseTitle = courseTitle,
                type = assignment.Type,
                language = assignment.Language,
                rating = assignment.Rating,
                difficulty = assignment.Difficulty,
                attempts = rows.Count,
                solved = rows.Count(x => x.Passed),
                averageScore = avgScore,
                uniqueUsers,
                successUsers,
                codeAttempts = 0,
                passedCodeAttempts = 0,
                testAttempts = testRows.Count,
                passedTests = testRows.Count(x => x.Passed),
                imageAttempts = 0,
                passedImages = 0,
                mathAttempts = mathRows.Count,
                passedMath = mathRows.Count(x => x.Passed),
                avgReviewSeconds = rows.Count == 0 ? 0 : System.Math.Round(rows.Average(x => System.Math.Max(0, ((x.SubmittedAt ?? x.UpdatedAt) - x.StartedAt).TotalSeconds)), 1),
                avgTestScore = avgScore,
                languages = string.IsNullOrWhiteSpace(assignment.Language) ? Array.Empty<object>() : new object[] { new { label = assignment.Language, value = rows.Count } },
                solvers,
                recentActivity = recent
            });
        });

        return app;
    }
}
