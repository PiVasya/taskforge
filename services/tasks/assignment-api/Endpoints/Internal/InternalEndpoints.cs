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
    private static WebApplication MapInternalEndpoints(WebApplication app)
    {
        app.MapGet("/api/internal/assignments/{assignmentId:guid}/judge-spec", async (Guid assignmentId, TasksDbContext db) =>
        {
            var assignment = await db.Assignments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == assignmentId);
            if (assignment == null)
            {
                return Microsoft.AspNetCore.Http.Results.NotFound(new { message = "Задание не найдено.", code = "ASSIGNMENT_NOT_FOUND" });
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
            var canView = assignment.IsVisible && courseAccess?.CanView == true;
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

            var key = TaskForgeCache.Key("tasks:assignment-summaries:v2", ids);
            var rows = await TaskForgeCache.GetOrSetAsync(cache, cfg, logger, key, TaskForgeCache.Ttl(cfg, "Metadata", 300), async token =>
            {
                var assignments = await db.Assignments.AsNoTracking().Where(x => ids.Contains(x.Id)).ToListAsync(token);
                return assignments.Select(ToAssignmentSummaryDto).ToList();
            }, ct);
            return Microsoft.AspNetCore.Http.Results.Ok(rows);
        });

        app.MapGet("/api/internal/users/{userId:guid}/activity-summary", async (Guid userId, TasksDbContext db, CancellationToken ct) =>
        {
            var testAttempts = await db.Attempts.AsNoTracking().Where(x => x.UserId == userId && x.Kind == "test").ToListAsync(ct);
            var mathAttempts = await db.Attempts.AsNoTracking().Where(x => x.UserId == userId && x.Kind == "math").ToListAsync(ct);
            var solved = testAttempts.Where(x => x.Passed).Select(x => x.TaskAssignmentId)
                .Concat(mathAttempts.Where(x => x.Passed).Select(x => x.TaskAssignmentId))
                .Distinct()
                .Count();
            return Microsoft.AspNetCore.Http.Results.Ok(new
            {
                solvedAssignments = solved,
                totalAttempts = testAttempts.Count + mathAttempts.Count,
                codeSolutions = 0,
                imageSolutions = 0,
                testAttempts = testAttempts.Count,
                mathAttempts = mathAttempts.Count
            });
        });

        app.MapPost("/api/internal/activity/leaderboard", async (ActivityLeaderboardRequest request, TasksDbContext db, CancellationToken ct) =>
        {
            var since = request.Days.HasValue && request.Days.Value > 0 ? DateTimeOffset.UtcNow.AddDays(-request.Days.Value) : (DateTimeOffset?)null;
            var userFilter = (request.UserIds ?? Array.Empty<Guid>()).Where(x => x != Guid.Empty).Distinct().ToHashSet();
            var q = db.Attempts.AsNoTracking().Where(x => x.Passed);
            if (since.HasValue) q = q.Where(x => x.SubmittedAt.HasValue && x.SubmittedAt.Value >= since.Value);
            if (userFilter.Count > 0) q = q.Where(x => userFilter.Contains(x.UserId));

            var joined = await q.Join(db.Assignments.AsNoTracking(), a => a.TaskAssignmentId, assignment => assignment.Id, (a, assignment) => new { Attempt = a, Assignment = assignment })
                .Where(x => !request.CourseId.HasValue || x.Assignment.CourseId == request.CourseId.Value)
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
}
