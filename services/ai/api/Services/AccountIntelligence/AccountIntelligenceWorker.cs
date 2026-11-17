using Microsoft.EntityFrameworkCore;
using TaskForge.Ai.Api.Data;
using TaskForge.Shared.Import;

namespace TaskForge.Ai.Api.Services.AccountIntelligence;

// Every API replica may run this worker. A PostgreSQL session lock protects each
// run across nodes. A crashed owner releases its lock, so a different replica
// can recover the run without resetting another replica's live work on startup.
internal sealed class AccountIntelligenceWorker(
    IServiceScopeFactory scopeFactory,
    AccountIntelligenceScanner scanner,
    ILogger<AccountIntelligenceWorker> logger) : BackgroundService
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
                logger.LogError(ex, "Account intelligence worker loop failed");
            }

            try { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task<bool> RunOneAsync(CancellationToken ct)
    {
        await using var scanScope = scopeFactory.CreateAsyncScope();
        var scanDb = scanScope.ServiceProvider.GetRequiredService<AiDbContext>();
        var ids = await scanDb.AccountAnalysisRuns.AsNoTracking()
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
            await using var guard = await PostgresImportLock.TryAcquireAsync(lockDb, $"ai-account-analysis:{id:N}", ct);
            if (guard is null) continue;

            // Lock first, then inspect current state. A completed run can never
            // be revived by a worker that observed an older snapshot.
            var updated = await lockDb.AccountAnalysisRuns
                .Where(x => x.Id == id && (x.Status == "queued" || x.Status == "starting" || x.Status == "running"))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.Status, "starting")
                    .SetProperty(x => x.Phase, "starting")
                    .SetProperty(x => x.ProgressPercent, 0)
                    .SetProperty(x => x.ErrorJson, (string?)null)
                    .SetProperty(x => x.UpdatedAtUtc, DateTimeOffset.UtcNow), ct);
            if (updated == 0) continue;

            logger.LogInformation("Claimed account analysis {RunId} under PostgreSQL advisory lock", id);
            await AccountWorkerLease.RunAsync(lockDb, token => scanner.ProcessRunAsync(id, token), logger, ct);
            return true;
        }
        return false;
    }
}
