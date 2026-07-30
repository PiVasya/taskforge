using Microsoft.EntityFrameworkCore;
using TaskForge.Ai.Api.Data;

namespace TaskForge.Ai.Api.Services.AccountIntelligence;

internal sealed class AccountIntelligenceWorker(
    IServiceScopeFactory scopeFactory,
    AccountIntelligenceScanner scanner,
    ILogger<AccountIntelligenceWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RecoverInterruptedRunsAsync(stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var runId = await ClaimNextRunAsync(stoppingToken);
                if (runId.HasValue)
                {
                    await scanner.ProcessRunAsync(runId.Value, stoppingToken);
                    continue;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Account intelligence worker loop failed");
            }

            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        }
    }

    private async Task<Guid?> ClaimNextRunAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AiDbContext>();
        var run = await db.AccountAnalysisRuns
            .Where(x => x.Status == "queued")
            .OrderBy(x => x.CreatedAtUtc)
            .FirstOrDefaultAsync(ct);
        if (run == null) return null;

        run.Status = "starting";
        run.Phase = "starting";
        run.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return run.Id;
    }

    private async Task RecoverInterruptedRunsAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AiDbContext>();
        // A fresh process cannot still own an in-memory scan from the previous process. Recover every
        // starting/running row immediately instead of leaving a recent row stuck forever after a restart.
        var stale = await db.AccountAnalysisRuns
            .Where(x => x.Status == "running" || x.Status == "starting")
            .ToListAsync(ct);
        foreach (var run in stale)
        {
            run.Status = "queued";
            run.Phase = "recovered-after-restart";
            run.ProgressPercent = 0;
            run.ErrorJson = null;
            run.UpdatedAtUtc = DateTimeOffset.UtcNow;
        }
        if (stale.Count > 0)
        {
            await db.SaveChangesAsync(ct);
            logger.LogWarning("Recovered {Count} interrupted account intelligence runs", stale.Count);
        }
    }
}
