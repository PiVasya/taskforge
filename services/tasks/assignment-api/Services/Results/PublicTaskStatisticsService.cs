using Npgsql;
using NpgsqlTypes;
using TaskForge.Tasks.Api.Data;

namespace TaskForge.Tasks.Api.Services.Results;

// Public profiles only receive aggregated verdicts. No answers, attempt IDs or
// private task material leave this internal projection.
internal sealed record PublicTaskStatisticsDay(string Day, int Accepted, int Failed, int Pending);
internal sealed record PublicTaskStatisticsKind(string Kind, long Attempts, long Accepted, long Failed, long Pending);
internal sealed record PublicTaskStatistics(
    long ResultAttempts, long ResultAccepted, long ResultFailed, long ResultPending,
    IReadOnlyList<PublicTaskStatisticsDay> DailyResults,
    IReadOnlyList<PublicTaskStatisticsKind> ResultTypes);

internal static class PublicTaskStatisticsService
{
    internal const int HistoryDays = 90;

    internal static string Verdict(bool passed, bool submitted, bool expired)
        => passed && (submitted || expired) ? "accepted"
            : !submitted && !expired ? "pending" : "failed";

    internal static async Task<PublicTaskStatistics> LoadAsync(TasksDbContext db, Guid userId, CancellationToken ct)
    {
        var cutoff = new DateTimeOffset(DateTime.UtcNow.Date.AddDays(-(HistoryDays - 1)), TimeSpan.Zero);
        var totals = new Dictionary<string, (long Accepted, long Failed, long Pending)>(StringComparer.Ordinal);
        var daily = new Dictionary<string, (int Accepted, int Failed, int Pending)>(StringComparer.Ordinal);

        await db.Database.OpenConnectionAsync(ct);
        try
        {
            var connection = (NpgsqlConnection)db.Database.GetDbConnection();
            // Database-side aggregation; never read every historic answer into the API.
            await using (var command = new NpgsqlCommand("""
                SELECT "Kind", "Passed", ("SubmittedAt" IS NOT NULL), "TimeExpired", COUNT(*)
                FROM "TaskAttempts"
                WHERE "UserId" = @userId AND "Kind" IN ('test', 'math')
                GROUP BY 1, 2, 3, 4
                """, connection))
            {
                command.Parameters.AddWithValue("userId", userId);
                await using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    var kind = reader.GetString(0);
                    var category = Verdict(reader.GetBoolean(1), reader.GetBoolean(2), reader.GetBoolean(3));
                    var count = reader.GetInt64(4);
                    totals.TryGetValue(kind, out var current);
                    totals[kind] = category switch
                    {
                        "accepted" => (current.Accepted + count, current.Failed, current.Pending),
                        "pending" => (current.Accepted, current.Failed, current.Pending + count),
                        _ => (current.Accepted, current.Failed + count, current.Pending)
                    };
                }
            }

            // A bounded calendar window, still grouped entirely inside PostgreSQL.
            await using (var command = new NpgsqlCommand("""
                SELECT to_char((COALESCE("SubmittedAt", "CreatedAt") AT TIME ZONE 'UTC')::date, 'YYYY-MM-DD'),
                       "Passed", ("SubmittedAt" IS NOT NULL), "TimeExpired", COUNT(*)
                FROM "TaskAttempts"
                WHERE "UserId" = @userId AND "Kind" IN ('test', 'math')
                  AND COALESCE("SubmittedAt", "CreatedAt") >= @since
                GROUP BY 1, 2, 3, 4
                """, connection))
            {
                command.Parameters.AddWithValue("userId", userId);
                command.Parameters.Add("since", NpgsqlDbType.TimestampTz).Value = cutoff;
                await using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    var day = reader.GetString(0);
                    var category = Verdict(reader.GetBoolean(1), reader.GetBoolean(2), reader.GetBoolean(3));
                    var count = checked((int)reader.GetInt64(4));
                    daily.TryGetValue(day, out var current);
                    daily[day] = category switch
                    {
                        "accepted" => (checked(current.Accepted + count), current.Failed, current.Pending),
                        "pending" => (current.Accepted, current.Failed, checked(current.Pending + count)),
                        _ => (current.Accepted, checked(current.Failed + count), current.Pending)
                    };
                }
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }

        var kinds = totals.OrderBy(x => x.Key, StringComparer.Ordinal)
            .Select(x => new PublicTaskStatisticsKind(x.Key,
                x.Value.Accepted + x.Value.Failed + x.Value.Pending,
                x.Value.Accepted, x.Value.Failed, x.Value.Pending)).ToArray();
        return new PublicTaskStatistics(
            kinds.Sum(x => x.Attempts), kinds.Sum(x => x.Accepted),
            kinds.Sum(x => x.Failed), kinds.Sum(x => x.Pending),
            daily.OrderBy(x => x.Key, StringComparer.Ordinal)
                .Select(x => new PublicTaskStatisticsDay(x.Key, x.Value.Accepted, x.Value.Failed, x.Value.Pending)).ToArray(),
            kinds);
    }
}
