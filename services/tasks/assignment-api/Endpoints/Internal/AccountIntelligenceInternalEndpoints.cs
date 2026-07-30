using Microsoft.EntityFrameworkCore;
using TaskForge.Tasks.Api.Data;

namespace TaskForge.Tasks.Api.Endpoints;

internal static partial class AssignmentApiEndpoints
{
    private static WebApplication MapAccountIntelligenceInternalEndpoints(WebApplication app)
    {
        app.MapGet("/api/internal/account-intelligence/tasks-snapshot", async (
            TasksDbContext db,
            int days = 365,
            CancellationToken ct = default) =>
        {
            days = Math.Clamp(days, 30, 1095);
            var since = DateTimeOffset.UtcNow.AddDays(-days);
            var technicalSince = DateTimeOffset.UtcNow.AddDays(-Math.Min(days, 180));
            const int attemptSampleLimit = 400_000;
            const int sessionSampleLimit = 250_000;
            const int technicalSampleLimit = 150_000;

            var attempts = await db.Attempts.AsNoTracking()
                .Where(x => x.CreatedAt >= since || (x.SubmittedAt.HasValue && x.SubmittedAt.Value >= since))
                .OrderByDescending(x => x.SubmittedAt ?? x.UpdatedAt)
                .Take(attemptSampleLimit)
                .Select(x => new
                {
                    x.UserId,
                    AssignmentId = x.TaskAssignmentId,
                    x.Kind,
                    x.Passed,
                    ActivityAt = x.SubmittedAt ?? x.UpdatedAt,
                })
                .ToListAsync(ct);

            var sessions = await db.AssignmentWorkSessions.AsNoTracking()
                .Where(x => x.LastActivityAt >= since)
                .OrderByDescending(x => x.LastActivityAt)
                .Take(sessionSampleLimit)
                .Select(x => new
                {
                    x.UserId,
                    x.AssignmentId,
                    x.LastActivityAt,
                    x.ActiveDurationMs,
                    x.SubmitCount,
                    x.PassedSubmitCount,
                    x.RiskScore,
                })
                .ToListAsync(ct);

            var eventHashes = await db.AssignmentActivityEvents.AsNoTracking()
                .Where(x => x.CreatedAt >= technicalSince && (x.IpHash != null || x.UserAgentHash != null))
                .OrderByDescending(x => x.CreatedAt)
                .Take(technicalSampleLimit)
                .Select(x => new { x.UserId, x.CreatedAt, x.IpHash, x.UserAgentHash })
                .ToListAsync(ct);

            var userIds = attempts.Select(x => x.UserId)
                .Concat(sessions.Select(x => x.UserId))
                .Concat(eventHashes.Select(x => x.UserId))
                .Distinct()
                .ToArray();

            var attemptGroups = attempts.GroupBy(x => x.UserId).ToDictionary(x => x.Key, x => x.ToList());
            var sessionGroups = sessions.GroupBy(x => x.UserId).ToDictionary(x => x.Key, x => x.ToList());
            var hashGroups = eventHashes.GroupBy(x => x.UserId).ToDictionary(x => x.Key, x => x.ToList());

            var items = userIds.Select(userId =>
            {
                var a = attemptGroups.GetValueOrDefault(userId) ?? [];
                var s = sessionGroups.GetValueOrDefault(userId) ?? [];
                var h = hashGroups.GetValueOrDefault(userId) ?? [];
                var activityTimes = a.Select(x => x.ActivityAt).Concat(s.Select(x => x.LastActivityAt)).ToArray();
                var assignmentIds = a.Select(x => x.AssignmentId).Concat(s.Select(x => x.AssignmentId)).Distinct().Take(1000).ToArray();
                return new
                {
                    userId,
                    totalAttempts = a.Count,
                    passedAttempts = a.Count(x => x.Passed),
                    testAttempts = a.Count(x => string.Equals(x.Kind, "test", StringComparison.OrdinalIgnoreCase)),
                    mathAttempts = a.Count(x => string.Equals(x.Kind, "math", StringComparison.OrdinalIgnoreCase)),
                    workSessions = s.Count,
                    submissionsFromSessions = s.Sum(x => x.SubmitCount),
                    passedSubmissionsFromSessions = s.Sum(x => x.PassedSubmitCount),
                    totalActiveDurationMs = s.Sum(x => x.ActiveDurationMs),
                    maxRiskScore = s.Count == 0 ? 0 : s.Max(x => x.RiskScore),
                    lastActivityAt = activityTimes.Length == 0 ? (DateTimeOffset?)null : activityTimes.Max(),
                    activeDays = activityTimes.Select(x => x.UtcDateTime.Date).Distinct().Count(),
                    assignmentIds,
                    ipHashes = h.Select(x => x.IpHash).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).Take(128).ToArray(),
                    userAgentHashes = h.Select(x => x.UserAgentHash).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).Take(128).ToArray(),
                };
            }).ToList();

            return Results.Ok(new
            {
                generatedAtUtc = DateTimeOffset.UtcNow,
                periodDays = days,
                technicalPeriodDays = Math.Min(days, 180),
                attemptSampleLimit,
                sampledAttempts = attempts.Count,
                sessionSampleLimit,
                sampledSessions = sessions.Count,
                technicalSampleLimit,
                sampledTechnicalEvents = eventHashes.Count,
                items
            });
        });

        return app;
    }
}
