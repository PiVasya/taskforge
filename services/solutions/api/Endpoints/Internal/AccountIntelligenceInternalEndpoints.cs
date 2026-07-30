using Microsoft.EntityFrameworkCore;
using TaskForge.Solutions.Api.Data;

namespace TaskForge.Solutions.Api.Endpoints;

internal static partial class SolutionsApiEndpoints
{
    private static WebApplication MapAccountIntelligenceInternalEndpoints(WebApplication app)
    {
        app.MapGet("/api/internal/account-intelligence/solutions-snapshot", async (
            SolutionsDbContext db,
            int days = 365,
            CancellationToken ct = default) =>
        {
            days = Math.Clamp(days, 30, 1095);
            var since = DateTimeOffset.UtcNow.AddDays(-days);
            const int codeSampleLimit = 400_000;
            const int imageSampleLimit = 250_000;

            var submissions = await db.Submissions.AsNoTracking()
                .Where(x => x.UserId.HasValue && x.CreatedAt >= since)
                .OrderByDescending(x => x.CreatedAt)
                .Take(codeSampleLimit)
                .Select(x => new
                {
                    UserId = x.UserId!.Value,
                    x.AssignmentId,
                    x.Status,
                    x.Score,
                    x.CreatedAt,
                    Kind = "code",
                })
                .ToListAsync(ct);

            var images = await db.ImageSolutions.AsNoTracking()
                .Where(x => x.CreatedAt >= since)
                .OrderByDescending(x => x.CreatedAt)
                .Take(imageSampleLimit)
                .Select(x => new
                {
                    x.UserId,
                    x.AssignmentId,
                    Status = x.Passed ? "Accepted" : "Rejected",
                    Score = x.SimilarityPercent,
                    x.CreatedAt,
                    Kind = "image",
                })
                .ToListAsync(ct);

            var ratings = await db.UserRatings.AsNoTracking().ToListAsync(ct);
            var ratingByUser = ratings.ToDictionary(x => x.UserId);
            var all = submissions.Concat(images).ToList();
            var groups = all.GroupBy(x => x.UserId).ToDictionary(x => x.Key, x => x.ToList());
            var userIds = groups.Keys.Concat(ratingByUser.Keys).Distinct().ToArray();

            var items = userIds.Select(userId =>
            {
                var rows = groups.GetValueOrDefault(userId) ?? [];
                ratingByUser.TryGetValue(userId, out var rating);
                return new
                {
                    userId,
                    totalAttempts = rows.Count,
                    acceptedAttempts = rows.Count(x => string.Equals(x.Status, "Accepted", StringComparison.OrdinalIgnoreCase)),
                    codeAttempts = rows.Count(x => x.Kind == "code"),
                    imageAttempts = rows.Count(x => x.Kind == "image"),
                    distinctAssignments = rows.Select(x => x.AssignmentId).Distinct().Count(),
                    acceptedAssignmentIds = rows.Where(x => string.Equals(x.Status, "Accepted", StringComparison.OrdinalIgnoreCase)).Select(x => x.AssignmentId).Distinct().Take(1000).ToArray(),
                    assignmentIds = rows.Select(x => x.AssignmentId).Distinct().Take(1000).ToArray(),
                    lastActivityAt = rows.Count == 0 ? rating?.UpdatedAt : rows.Max(x => x.CreatedAt),
                    activeDays = rows.Select(x => x.CreatedAt.UtcDateTime.Date).Distinct().Count(),
                    totalScore = rating?.TotalScore ?? 0,
                    solvedCount = rating?.SolvedCount ?? 0,
                    ratingAttempts = rating?.AttemptsCount ?? rows.Count,
                    lastAcceptedAt = rating?.LastAcceptedAt,
                };
            }).ToList();

            return Results.Ok(new
            {
                generatedAtUtc = DateTimeOffset.UtcNow,
                periodDays = days,
                codeSampleLimit,
                sampledCodeRows = submissions.Count,
                imageSampleLimit,
                sampledImageRows = images.Count,
                items
            });
        });

        return app;
    }
}
