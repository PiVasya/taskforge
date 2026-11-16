using Microsoft.EntityFrameworkCore;
using TaskForge.Tasks.Api.Data;

namespace TaskForge.Tasks.Api.Services.Testing;

internal static class TestAttemptStorageCleanupService
{
    // The old test attempts stored full authored specs in OrderJson and duplicated
    // question content in ReviewJson. Neither is needed: answers and scores remain
    // intact, while the current spec supplies the review at read time.
    internal const string CleanupSql = """
        WITH batch AS (
            SELECT "Id" FROM "TaskAttempts"
            WHERE "Kind" = 'test'
              AND (("OrderJson" IS NOT NULL AND jsonb_typeof("OrderJson") <> 'array')
                   OR "ReviewJson" IS NOT NULL)
            ORDER BY "Id"
            LIMIT 500
            FOR UPDATE SKIP LOCKED
        )
        UPDATE "TaskAttempts" AS attempt
        SET "OrderJson" = CASE
                WHEN attempt."OrderJson" IS NULL THEN NULL
                WHEN jsonb_typeof(attempt."OrderJson") = 'array' THEN attempt."OrderJson"
                WHEN jsonb_typeof(attempt."OrderJson"->'order') = 'array' THEN attempt."OrderJson"->'order'
                ELSE '[]'::jsonb
            END,
            "ReviewJson" = NULL
        FROM batch
        WHERE attempt."Id" = batch."Id"
        """;

    internal static async Task CleanupAsync(TasksDbContext db, ILogger logger, CancellationToken ct = default)
    {
        var updated = 0;
        while (true)
        {
            var batch = await db.Database.ExecuteSqlRawAsync(CleanupSql, ct);
            updated += batch;
            if (batch < 500) break;
        }
        if (updated > 0)
            logger.LogInformation("Removed old test question copies from {Count} existing attempts.", updated);
    }
}
