using Npgsql;

namespace TaskForge.Solutions.RatingWorker;

public sealed class Worker(ILogger<Worker> logger, IConfiguration configuration) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Rating worker started. It periodically rebuilds UserRatings from terminal solution submissions.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var updated = await RebuildRatingsAsync(stoppingToken);
                logger.LogInformation("Rating projection rebuilt. Affected users: {Count}.", updated);
                await Task.Delay(TimeSpan.FromMinutes(System.Math.Clamp(configuration.GetValue("Rating:RebuildIntervalMinutes", 5), 1, 60)), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Rating projection rebuild failed.");
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
        }
    }

    private async Task<int> RebuildRatingsAsync(CancellationToken ct)
    {
        var connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("ConnectionStrings:DefaultConnection is not configured.");

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);

        const string cleanupSql = """
            DELETE FROM "UserRatings"
            WHERE "UserId" NOT IN (
                SELECT DISTINCT "UserId"
                FROM "SolutionSubmissions"
                WHERE "UserId" IS NOT NULL
                  AND "Status" IN ('Accepted', 'Rejected', 'CompileError')
            );
            """;

        const string rebuildSql = """
            INSERT INTO "UserRatings" (
                "UserId", "TotalScore", "SolvedCount", "AttemptsCount",
                "AcceptedCount", "RejectedCount", "LastAcceptedAt", "UpdatedAt"
            )
            SELECT
                "UserId",
                COALESCE(SUM(CASE WHEN "Status" = 'Accepted' THEN "Score" ELSE 0 END), 0)::integer AS "TotalScore",
                COUNT(*) FILTER (WHERE "Status" = 'Accepted')::integer AS "SolvedCount",
                COUNT(*)::integer AS "AttemptsCount",
                COUNT(*) FILTER (WHERE "Status" = 'Accepted')::integer AS "AcceptedCount",
                COUNT(*) FILTER (WHERE "Status" <> 'Accepted')::integer AS "RejectedCount",
                MAX("CreatedAt") FILTER (WHERE "Status" = 'Accepted') AS "LastAcceptedAt",
                NOW() AS "UpdatedAt"
            FROM "SolutionSubmissions"
            WHERE "UserId" IS NOT NULL
              AND "Status" IN ('Accepted', 'Rejected', 'CompileError')
            GROUP BY "UserId"
            ON CONFLICT ("UserId") DO UPDATE SET
                "TotalScore" = EXCLUDED."TotalScore",
                "SolvedCount" = EXCLUDED."SolvedCount",
                "AttemptsCount" = EXCLUDED."AttemptsCount",
                "AcceptedCount" = EXCLUDED."AcceptedCount",
                "RejectedCount" = EXCLUDED."RejectedCount",
                "LastAcceptedAt" = EXCLUDED."LastAcceptedAt",
                "UpdatedAt" = EXCLUDED."UpdatedAt";
            """;

        await using (var cleanup = new NpgsqlCommand(cleanupSql, connection, tx))
        {
            await cleanup.ExecuteNonQueryAsync(ct);
        }

        int affected;
        await using (var rebuild = new NpgsqlCommand(rebuildSql, connection, tx))
        {
            affected = await rebuild.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return affected;
    }
}
