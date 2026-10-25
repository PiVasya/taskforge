using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using TaskForge.Solutions.Api.Data;

namespace TaskForge.Solutions.Api.Services.Results;

internal sealed record PublicStatisticsDay(string Day, int Accepted, int Failed, int Pending);
internal sealed record PublicRatingDay(string Day, int Tasks, long Rating);
internal sealed record PublicStatisticsLanguage(string Language, long Attempts, long Accepted);
internal sealed record PublicStatisticsSource(string Kind, long AcceptedAttempts);
internal sealed record PublicStatisticsResultType(string Kind, long Attempts, long Accepted, long Failed, long Pending);
internal sealed record UserPublicStatistics(
    int Rating, int SolvedAssignments, int TotalAttempts,
    long CodeAttempts, long CodeAccepted, long CodeFailed, long CodePending,
    DateTimeOffset? LastAcceptedAt,
    IReadOnlyList<PublicStatisticsDay> DailyCode,
    IReadOnlyList<PublicRatingDay> DailyRating,
    IReadOnlyList<PublicStatisticsLanguage> Languages,
    IReadOnlyList<PublicStatisticsSource> AcceptedSources)
{
    // These totals cover submissions and images in this service. Identity
    // combines them with math/test verdicts from Tasks API before publishing.
    public long ResultAttempts { get; init; }
    public long ResultAccepted { get; init; }
    public long ResultFailed { get; init; }
    public long ResultPending { get; init; }
    public IReadOnlyList<PublicStatisticsDay> DailyResults { get; init; } = [];
    public IReadOnlyList<PublicStatisticsResultType> ResultTypes { get; init; } = [];
}

// This projection is internal only. Identity checks public-profile and showStats
// preferences before exposing its aggregate output to a browser.
internal static class UserPublicStatisticsService
{
    internal const int HistoryDays = 90;

    internal static string VerdictCategory(string? status)
    {
        if (string.Equals(status, "Accepted", StringComparison.OrdinalIgnoreCase)) return "accepted";
        if (string.IsNullOrWhiteSpace(status) || status.Equals("Preparing", StringComparison.OrdinalIgnoreCase)
            || status.Equals("Queued", StringComparison.OrdinalIgnoreCase)
            || status.Equals("Running", StringComparison.OrdinalIgnoreCase)) return "pending";
        return "failed";
    }

    internal static string NormalizeLanguage(string? language)
    {
        var key = (language ?? string.Empty).Trim().ToLowerInvariant();
        return key switch
        {
            "c++" or "cxx" or "cc" => "cpp",
            "c#" => "csharp",
            "js" => "javascript",
            "py" => "python",
            "postgresql" or "mysql" or "sqlite" => "sql",
            "" => "unknown",
            _ => key.Length <= 40 ? key : key[..40]
        };
    }

    internal static async Task<UserPublicStatistics?> LoadAsync(SolutionsDbContext db, Guid userId, CancellationToken ct)
    {
        // Don't mistake an unbuilt read model for a user with no activity.
        if (!await LeaderboardReadModelService.IsReadyAsync(db, ct)) return null;

        var rating = await db.UserRatings.AsNoTracking().FirstOrDefaultAsync(x => x.UserId == userId, ct);
        var cutoff = new DateTimeOffset(DateTime.UtcNow.Date.AddDays(-(HistoryDays - 1)), TimeSpan.Zero);
        var daily = new Dictionary<string, (int Accepted, int Failed, int Pending)>(StringComparer.Ordinal);
        var dailyResults = new Dictionary<string, (int Accepted, int Failed, int Pending)>(StringComparer.Ordinal);
        var types = new Dictionary<string, (long Accepted, long Failed, long Pending)>(StringComparer.Ordinal);
        void AddType(string kind, string verdict, long amount)
        {
            types.TryGetValue(kind, out var current);
            types[kind] = verdict switch
            {
                "accepted" => (current.Accepted + amount, current.Failed, current.Pending),
                "pending" => (current.Accepted, current.Failed, current.Pending + amount),
                _ => (current.Accepted, current.Failed + amount, current.Pending)
            };
        }
        var awards = new List<PublicRatingDay>();
        var languages = new Dictionary<string, (long Attempts, long Accepted)>(StringComparer.Ordinal);
        var sources = new List<PublicStatisticsSource>();

        await db.Database.OpenConnectionAsync(ct);
        try
        {
            var connection = (NpgsqlConnection)db.Database.GetDbConnection();

            // One bounded, indexed history window. No solution code, identifiers,
            // private verdict details, or other users' records leave this query.
            await using (var command = new NpgsqlCommand("""
                SELECT to_char(("CreatedAt" AT TIME ZONE 'UTC')::date, 'YYYY-MM-DD') AS day,
                       "Status", COUNT(*)
                FROM "SolutionSubmissions"
                WHERE "UserId" = @userId AND "CreatedAt" >= @since
                GROUP BY 1, 2
                ORDER BY 1
                """, connection))
            {
                command.Parameters.AddWithValue("userId", userId);
                command.Parameters.Add("since", NpgsqlDbType.TimestampTz).Value = cutoff;
                await using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    var day = reader.GetString(0);
                    var category = VerdictCategory(reader.IsDBNull(1) ? null : reader.GetString(1));
                    var count = checked((int)reader.GetInt64(2));
                    daily.TryGetValue(day, out var current);
                    daily[day] = category switch
                    {
                        "accepted" => (checked(current.Accepted + count), current.Failed, current.Pending),
                        "pending" => (current.Accepted, current.Failed, checked(current.Pending + count)),
                        _ => (current.Accepted, checked(current.Failed + count), current.Pending)
                    };
                    dailyResults[day] = daily[day];
                }
            }

            // The SQL distinction uses the pinned assignment revision as well
            // as the language, so submitted SQL is not presented as generic code.
            long codeAttempts = 0, codeAccepted = 0, codeFailed = 0, codePending = 0;
            await using (var command = new NpgsqlCommand("""
                SELECT CASE WHEN "SqlSpecVersionId" IS NOT NULL OR "SqlEngineProfileId" IS NOT NULL
                              OR lower("Language") IN ('sql', 'postgresql', 'mysql', 'sqlite')
                            THEN 'sql' ELSE 'code' END,
                       "Language", "Status", COUNT(*)
                FROM "SolutionSubmissions"
                WHERE "UserId" = @userId
                GROUP BY 1, 2, 3
                """, connection))
            {
                command.Parameters.AddWithValue("userId", userId);
                await using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    var kind = reader.GetString(0);
                    var language = NormalizeLanguage(reader.IsDBNull(1) ? null : reader.GetString(1));
                    var category = VerdictCategory(reader.IsDBNull(2) ? null : reader.GetString(2));
                    var count = reader.GetInt64(3);
                    codeAttempts += count;
                    if (category == "accepted") codeAccepted += count;
                    else if (category == "pending") codePending += count;
                    else codeFailed += count;
                    AddType(kind, category, count);
                    languages.TryGetValue(language, out var current);
                    languages[language] = (current.Attempts + count,
                        current.Accepted + (category == "accepted" ? count : 0));
                }
            }

            // Image submissions live in SolutionsDb too. They had been omitted
            // from both graphs even though they are real checked attempts.
            await using (var command = new NpgsqlCommand("""
                SELECT "Passed", COUNT(*)
                FROM "UserImageTaskSolutions"
                WHERE "UserId" = @userId
                GROUP BY "Passed"
                """, connection))
            {
                command.Parameters.AddWithValue("userId", userId);
                await using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                    AddType("image", reader.GetBoolean(0) ? "accepted" : "failed", reader.GetInt64(1));
            }

            await using (var command = new NpgsqlCommand("""
                SELECT to_char(("CreatedAt" AT TIME ZONE 'UTC')::date, 'YYYY-MM-DD'),
                       "Passed", COUNT(*)
                FROM "UserImageTaskSolutions"
                WHERE "UserId" = @userId AND "CreatedAt" >= @since
                GROUP BY 1, 2
                """, connection))
            {
                command.Parameters.AddWithValue("userId", userId);
                command.Parameters.Add("since", NpgsqlDbType.TimestampTz).Value = cutoff;
                await using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    var day = reader.GetString(0);
                    var passed = reader.GetBoolean(1);
                    var count = checked((int)reader.GetInt64(2));
                    dailyResults.TryGetValue(day, out var current);
                    dailyResults[day] = passed
                        ? (checked(current.Accepted + count), current.Failed, current.Pending)
                        : (current.Accepted, checked(current.Failed + count), current.Pending);
                }
            }

            // Award a task only once, on its first accepted submission. Repeated
            // accepted attempts on the same assignment cannot inflate rating charts.
            await using (var command = new NpgsqlCommand("""
                SELECT to_char((first_at AT TIME ZONE 'UTC')::date, 'YYYY-MM-DD') AS day,
                       COUNT(*)::integer, COALESCE(SUM(rating), 0)::bigint
                FROM (
                    SELECT "AssignmentId", MIN("SubmittedAt") AS first_at,
                           MAX("Rating") AS rating
                    FROM "RatingAcceptedActivities"
                    WHERE "UserId" = @userId
                    GROUP BY "AssignmentId"
                ) AS earned
                WHERE first_at >= @since
                GROUP BY 1
                ORDER BY 1
                """, connection))
            {
                command.Parameters.AddWithValue("userId", userId);
                command.Parameters.Add("since", NpgsqlDbType.TimestampTz).Value = cutoff;
                await using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                    awards.Add(new PublicRatingDay(reader.GetString(0), reader.GetInt32(1), reader.GetInt64(2)));
            }

            await using (var command = new NpgsqlCommand("""
                SELECT "SourceKind", COUNT(*)
                FROM "RatingAcceptedActivities"
                WHERE "UserId" = @userId
                GROUP BY "SourceKind"
                ORDER BY 2 DESC
                """, connection))
            {
                command.Parameters.AddWithValue("userId", userId);
                await using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                    sources.Add(new PublicStatisticsSource(reader.GetString(0), reader.GetInt64(1)));
            }

            return new UserPublicStatistics(
                rating?.TotalScore ?? 0, rating?.SolvedCount ?? 0, rating?.AttemptsCount ?? 0,
                codeAttempts, codeAccepted, codeFailed, codePending,
                rating?.LastAcceptedAt,
                daily.OrderBy(x => x.Key, StringComparer.Ordinal)
                    .Select(x => new PublicStatisticsDay(x.Key, x.Value.Accepted, x.Value.Failed, x.Value.Pending))
                    .ToArray(),
                awards,
                languages.OrderByDescending(x => x.Value.Attempts).ThenBy(x => x.Key, StringComparer.Ordinal)
                    .Take(10).Select(x => new PublicStatisticsLanguage(x.Key, x.Value.Attempts, x.Value.Accepted)).ToArray(),
                sources)
            {
                ResultAttempts = types.Values.Sum(x => x.Accepted + x.Failed + x.Pending),
                ResultAccepted = types.Values.Sum(x => x.Accepted),
                ResultFailed = types.Values.Sum(x => x.Failed),
                ResultPending = types.Values.Sum(x => x.Pending),
                DailyResults = dailyResults.OrderBy(x => x.Key, StringComparer.Ordinal)
                    .Select(x => new PublicStatisticsDay(x.Key, x.Value.Accepted, x.Value.Failed, x.Value.Pending)).ToArray(),
                ResultTypes = types.OrderBy(x => x.Key, StringComparer.Ordinal)
                    .Select(x => new PublicStatisticsResultType(x.Key,
                        x.Value.Accepted + x.Value.Failed + x.Value.Pending,
                        x.Value.Accepted, x.Value.Failed, x.Value.Pending)).ToArray()
            };
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }
}
