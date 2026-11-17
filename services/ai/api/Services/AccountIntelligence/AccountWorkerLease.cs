using System.Data;
using Microsoft.EntityFrameworkCore;
using TaskForge.Ai.Api.Data;

namespace TaskForge.Ai.Api.Services.AccountIntelligence;

// A PostgreSQL session advisory lock is held in a dedicated DbContext for the
// whole operation. Unlike a TTL lease, it cannot expire during a slow HTTP call.
// Heartbeats cancel the workload if its connection (and thus lock) is lost.
internal static class AccountWorkerLease
{
    internal static async Task RunAsync(
        AiDbContext lockDb,
        Func<CancellationToken, Task> work,
        ILogger logger,
        CancellationToken stoppingToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var watcher = MonitorAsync(lockDb, linked, logger);
        try
        {
            await work(linked.Token);
        }
        finally
        {
            linked.Cancel();
            await watcher;
        }
    }

    private static async Task MonitorAsync(AiDbContext lockDb, CancellationTokenSource linked, ILogger logger)
    {
        try
        {
            while (!linked.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(3), linked.Token);
                if (lockDb.Database.GetDbConnection().State != ConnectionState.Open)
                    throw new InvalidOperationException("Account worker advisory-lock connection closed.");
                await lockDb.Database.ExecuteSqlRawAsync("SELECT 1", linked.Token);
            }
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
        catch (Exception ex)
        {
            logger.LogError(ex, "Lost account worker advisory-lock connection; cancelling active work");
            linked.Cancel();
        }
    }
}
