using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using TaskForge.Solutions.Api.Data;

namespace TaskForge.Solutions.Api.Services.Results;

internal sealed record LeaderboardRatingRow(Guid UserId, int Score, int SolvedAssignments, int TotalAttempts,
    DateTimeOffset? LastSubmitAt);

internal static class LeaderboardReadModelService
{
    internal const string ReadyCheckpoint = "RatingAcceptedActivitiesV1";

    internal static bool RequiresActivityProjection(Guid? courseId, int? days)
        => courseId.HasValue || (days.HasValue && days.Value > 0);

    internal static async Task<bool> IsReadyAsync(SolutionsDbContext db, CancellationToken ct)
        => await db.RatingProjectionCheckpoints.AsNoTracking().AnyAsync(
            x => x.ProjectionName == ReadyCheckpoint && x.LastEventSequence == 1, ct);

    internal static async Task<List<LeaderboardRatingRow>> LoadAsync(SolutionsDbContext db,
        Guid? courseId, int? days, Guid[]? groupUserIds, IReadOnlyCollection<Guid>? courseScopeIds, CancellationToken ct)
    {
        if (groupUserIds is { Length: 0 }) return [];
        if (!RequiresActivityProjection(courseId, days))
        {
            var query = db.UserRatings.AsNoTracking().Where(x => x.SolvedCount > 0);
            if (groupUserIds is { Length: > 0 }) query = query.Where(x => groupUserIds.Contains(x.UserId));
            return await query.OrderByDescending(x => x.TotalScore)
                .ThenByDescending(x => x.SolvedCount).ThenByDescending(x => x.LastAcceptedAt)
                .ThenBy(x => x.UserId)
                .Select(x => new LeaderboardRatingRow(x.UserId, x.TotalScore, x.SolvedCount,
                    x.AcceptedCount, x.LastAcceptedAt))
                .ToListAsync(ct);
        }

        // The activity records were validated and persisted by rating-worker.
        // Grouping happens in PostgreSQL; there are no raw verdict scans or
        // cross-service task metadata requests when a leaderboard is viewed.
        var courseIds = courseId.HasValue ? courseScopeIds?.Where(x => x != Guid.Empty).Distinct().ToArray() ?? [] : null;
        if (courseIds is { Length: 0 }) return [];
        var since = days is > 0 ? DateTimeOffset.UtcNow.AddDays(-Math.Clamp(days.Value, 1, 3650)) : (DateTimeOffset?)null;

        await db.Database.OpenConnectionAsync(ct);
        try
        {
            var connection = (NpgsqlConnection)db.Database.GetDbConnection();
            await using var command = new NpgsqlCommand("""
                SELECT x."UserId", SUM(x."Rating") AS "Score", COUNT(*) AS "Solved",
                       SUM(x."Attempts")::bigint AS "Attempts", MAX(x."LastSubmitAt") AS "LastSubmitAt"
                FROM (
                    SELECT "UserId", "AssignmentId", MAX("Rating") AS "Rating",
                           COUNT(*) AS "Attempts", MAX("SubmittedAt") AS "LastSubmitAt"
                    FROM "RatingAcceptedActivities"
                    WHERE (@sinceUtc::timestamptz IS NULL OR "SubmittedAt" >= @sinceUtc)
                      AND (@courseIds::uuid[] IS NULL OR "CourseId" = ANY(@courseIds))
                      AND (@groupIds::uuid[] IS NULL OR "UserId" = ANY(@groupIds))
                    GROUP BY "UserId", "AssignmentId"
                ) AS x
                GROUP BY x."UserId"
                ORDER BY "Score" DESC, "Solved" DESC, "LastSubmitAt" DESC, x."UserId";
                """, connection);
            command.Parameters.Add("sinceUtc", NpgsqlDbType.TimestampTz).Value = since.HasValue ? since.Value : DBNull.Value;
            command.Parameters.Add("courseIds", NpgsqlDbType.Array | NpgsqlDbType.Uuid).Value = courseIds is null ? DBNull.Value : courseIds;
            command.Parameters.Add("groupIds", NpgsqlDbType.Array | NpgsqlDbType.Uuid).Value = groupUserIds is null ? DBNull.Value : groupUserIds;

            var rows = new List<LeaderboardRatingRow>();
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                rows.Add(new LeaderboardRatingRow(reader.GetGuid(0),
                    checked((int)reader.GetInt64(1)),
                    checked((int)reader.GetInt64(2)),
                    checked((int)reader.GetInt64(3)),
                    reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4)));
            }
            return rows;
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }
}
