using Microsoft.EntityFrameworkCore;
using TaskForge.Tasks.Api.Data;

using static TaskForge.Tasks.Api.Services.Mapping.AssignmentApiMappingService;
using static TaskForge.Tasks.Api.Services.Results.AssignmentApiResultsService;

namespace TaskForge.Tasks.Api.Endpoints;

internal static partial class AssignmentApiEndpoints
{
    private static WebApplication MapAgentInvestigationInternalEndpoints(WebApplication app)
    {
        app.MapGet("/api/internal/agent/users/{userId:guid}/activity", async (
            Guid userId,
            DateTimeOffset? fromUtc,
            DateTimeOffset? toUtc,
            int take,
            TasksDbContext db,
            CancellationToken ct) =>
        {
            var to = toUtc ?? DateTimeOffset.UtcNow;
            var from = fromUtc ?? to.AddHours(-24);
            var limit = System.Math.Clamp(take <= 0 ? 200 : take, 1, 500);

            var rowsWithSentinel = await db.ActivityEvents.AsNoTracking()
                .Where(x => x.UserId == userId && x.CreatedAt >= from && x.CreatedAt <= to)
                .OrderByDescending(x => x.CreatedAt)
                .Take(limit + 1)
                .ToListAsync(ct);
            var truncated = rowsWithSentinel.Count > limit;
            var rows = rowsWithSentinel.Take(limit).ToList();

            var assignmentIds = rows.Select(x => x.AssignmentId).Where(x => x != Guid.Empty).Distinct().ToArray();
            var assignments = assignmentIds.Length == 0
                ? new Dictionary<Guid, TaskForge.Tasks.Api.Domain.Assignment>()
                : await db.Assignments.AsNoTracking().Where(x => assignmentIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);

            var events = rows.Select(x =>
            {
                assignments.TryGetValue(x.AssignmentId, out var assignment);
                return new
                {
                    id = x.Id,
                    x.UserId,
                    x.AssignmentId,
                    assignmentTitle = assignment?.Title ?? "Задание",
                    courseId = assignment?.CourseId ?? Guid.Empty,
                    assignmentType = assignment?.Type,
                    x.EventType,
                    x.CreatedAt,
                    createdAtUtc = x.CreatedAt,
                    x.ClientTime,
                    x.AttemptId,
                    x.SubmissionId,
                    x.Language,
                    x.CodeLength,
                    x.CodeDelta,
                    x.TextLength,
                    x.ActiveDurationMs,
                    x.HiddenDurationMs,
                    x.BlurDurationMs,
                    x.RiskPoints,
                    x.RiskReason,
                    assignmentUrl = $"/assignment/{x.AssignmentId:D}"
                };
            }).ToList();

            return Microsoft.AspNetCore.Http.Results.Ok(new
            {
                userId,
                fromUtc = from,
                toUtc = to,
                count = events.Count,
                limit,
                truncated,
                events
            });
        });

        app.MapGet("/api/internal/agent/users/{userId:guid}/attempts", async (
            Guid userId,
            Guid? assignmentId,
            DateTimeOffset? fromUtc,
            DateTimeOffset? toUtc,
            int take,
            TasksDbContext db,
            CancellationToken ct) =>
        {
            var to = toUtc ?? DateTimeOffset.UtcNow;
            var from = fromUtc ?? to.AddDays(-7);
            var limit = System.Math.Clamp(take <= 0 ? 200 : take, 1, 500);

            var query = db.Attempts.AsNoTracking()
                .Where(x => x.UserId == userId && x.SubmittedAt.HasValue && x.SubmittedAt.Value >= from && x.SubmittedAt.Value <= to);
            if (assignmentId.HasValue)
                query = query.Where(x => x.TaskAssignmentId == assignmentId.Value);

            var rowsWithSentinel = await query.OrderByDescending(x => x.SubmittedAt).Take(limit + 1).ToListAsync(ct);
            var truncated = rowsWithSentinel.Count > limit;
            var rows = rowsWithSentinel.Take(limit).ToList();
            var assignmentIds = rows.Select(x => x.TaskAssignmentId).Where(x => x != Guid.Empty).Distinct().ToArray();
            var assignments = assignmentIds.Length == 0
                ? new Dictionary<Guid, TaskForge.Tasks.Api.Domain.Assignment>()
                : await db.Assignments.AsNoTracking().Where(x => assignmentIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);

            var attempts = rows.Select(x =>
            {
                assignments.TryGetValue(x.TaskAssignmentId, out var assignment);
                return new
                {
                    id = x.Id,
                    attemptId = x.Id,
                    kind = x.Kind,
                    x.UserId,
                    assignmentId = x.TaskAssignmentId,
                    taskAssignmentId = x.TaskAssignmentId,
                    assignmentTitle = assignment?.Title ?? "Задание",
                    courseId = assignment?.CourseId ?? Guid.Empty,
                    assignmentType = assignment?.Type,
                    x.AttemptNumber,
                    submittedAtUtc = x.SubmittedAt,
                    x.ScorePercent,
                    x.Passed,
                    x.TimeExpired,
                    status = x.Passed ? "Accepted" : (x.TimeExpired ? "TimeExpired" : "Failed"),
                    assignmentUrl = $"/assignment/{x.TaskAssignmentId:D}"
                };
            }).ToList();

            return Microsoft.AspNetCore.Http.Results.Ok(new
            {
                userId,
                assignmentId,
                fromUtc = from,
                toUtc = to,
                count = attempts.Count,
                limit,
                truncated,
                attempts
            });
        });

        app.MapGet("/api/internal/agent/attempts/{kind}/{attemptId:guid}", async (
            string kind,
            Guid attemptId,
            TasksDbContext db) =>
        {
            var normalized = string.Equals(kind, "math", StringComparison.OrdinalIgnoreCase) ? "math" :
                string.Equals(kind, "test", StringComparison.OrdinalIgnoreCase) ? "test" : string.Empty;
            if (normalized.Length == 0)
                return Microsoft.AspNetCore.Http.Results.BadRequest(new { message = "Поддерживаются только test и math.", code = "ATTEMPT_KIND_INVALID" });
            return await ReviewAttempt(attemptId, normalized, null, true, db);
        });

        app.MapGet("/api/internal/agent/assignments/{assignmentId:guid}", async (
            Guid assignmentId,
            TasksDbContext db,
            CancellationToken ct) =>
        {
            var assignment = await db.Assignments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == assignmentId, ct);
            if (assignment == null)
                return Microsoft.AspNetCore.Http.Results.NotFound(new { message = "Задание не найдено.", code = "ASSIGNMENT_NOT_FOUND" });

            return Microsoft.AspNetCore.Http.Results.Ok(new
            {
                assignment = ToDto(assignment, includeSensitive: true),
                assignmentUrl = $"/assignment/{assignment.Id:D}",
                editUrl = $"/assignment/{assignment.Id:D}/edit"
            });
        });

        return app;
    }
}
