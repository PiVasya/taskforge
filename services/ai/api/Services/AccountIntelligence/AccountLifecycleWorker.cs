using Microsoft.EntityFrameworkCore;
using TaskForge.Ai.Api.Data;
using TaskForge.Shared.Import;

namespace TaskForge.Ai.Api.Services.AccountIntelligence;

// Multi-replica safe: an operation can be executed by only one API instance at
// a time. Recovery happens only after its PostgreSQL session lock is released.
// Completed step receipts remain intact, so the coordinator can resume them.
internal sealed class AccountLifecycleWorker(
    IServiceScopeFactory scopeFactory,
    AccountLifecycleCoordinator coordinator,
    ILogger<AccountLifecycleWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await RunOneAsync(stoppingToken)) continue;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Account lifecycle worker loop failed");
            }

            try { await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task<bool> RunOneAsync(CancellationToken ct)
    {
        await using var scanScope = scopeFactory.CreateAsyncScope();
        var scanDb = scanScope.ServiceProvider.GetRequiredService<AiDbContext>();
        var ids = await scanDb.AccountManagementOperations.AsNoTracking()
            .Where(x => x.Status == "queued" || x.Status == "starting" || x.Status == "running")
            .OrderBy(x => x.Status == "queued" ? 0 : 1)
            .ThenBy(x => x.CreatedAtUtc)
            .Select(x => x.Id)
            .Take(128)
            .ToArrayAsync(ct);

        foreach (var id in ids)
        {
            ct.ThrowIfCancellationRequested();
            await using var lockScope = scopeFactory.CreateAsyncScope();
            var lockDb = lockScope.ServiceProvider.GetRequiredService<AiDbContext>();
            await using var guard = await PostgresImportLock.TryAcquireAsync(lockDb, $"ai-account-lifecycle:{id:N}", ct);
            if (guard is null) continue;

            var operation = await lockDb.AccountManagementOperations.AsNoTracking()
                .Where(x => x.Id == id)
                .Select(x => new { x.Status, x.CancelRequested })
                .SingleOrDefaultAsync(ct);
            if (operation is null || operation.Status is not ("queued" or "starting" or "running")) continue;
            if (operation.CancelRequested)
            {
                await lockDb.AccountManagementOperations
                    .Where(x => x.Id == id && (x.Status == "queued" || x.Status == "starting" || x.Status == "running"))
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(x => x.Status, "cancelled")
                        .SetProperty(x => x.Phase, "cancelled")
                        .SetProperty(x => x.CompletedAtUtc, DateTimeOffset.UtcNow)
                        .SetProperty(x => x.UpdatedAtUtc, DateTimeOffset.UtcNow), ct);
                return true;
            }

            var updated = await lockDb.AccountManagementOperations
                .Where(x => x.Id == id && !x.CancelRequested &&
                    (x.Status == "queued" || x.Status == "starting" || x.Status == "running"))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.Status, "starting")
                    .SetProperty(x => x.Phase, "starting")
                    .SetProperty(x => x.UpdatedAtUtc, DateTimeOffset.UtcNow), ct);
            if (updated == 0) continue;

            logger.LogInformation("Claimed account lifecycle {OperationId} under PostgreSQL advisory lock", id);
            await AccountWorkerLease.RunAsync(lockDb, token => coordinator.ProcessOperationAsync(id, token), logger, ct);
            return true;
        }
        return false;
    }
}
