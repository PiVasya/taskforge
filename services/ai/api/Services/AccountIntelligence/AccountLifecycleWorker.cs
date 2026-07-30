using Microsoft.EntityFrameworkCore;
using TaskForge.Ai.Api.Data;

namespace TaskForge.Ai.Api.Services.AccountIntelligence;

internal sealed class AccountLifecycleWorker(
    IServiceScopeFactory scopeFactory,
    AccountLifecycleCoordinator coordinator,
    ILogger<AccountLifecycleWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RecoverInterruptedOperationsAsync(stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var operationId = await ClaimNextOperationAsync(stoppingToken);
                if (operationId.HasValue)
                {
                    await coordinator.ProcessOperationAsync(operationId.Value, stoppingToken);
                    continue;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Account lifecycle worker loop failed");
            }

            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
        }
    }

    private async Task<Guid?> ClaimNextOperationAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AiDbContext>();
        var operation = await db.AccountManagementOperations
            .Where(x => x.Status == "queued" && !x.CancelRequested)
            .OrderBy(x => x.CreatedAtUtc)
            .FirstOrDefaultAsync(ct);
        if (operation == null) return null;
        operation.Status = "starting";
        operation.Phase = "starting";
        operation.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return operation.Id;
    }

    private async Task RecoverInterruptedOperationsAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AiDbContext>();
        var interrupted = await db.AccountManagementOperations
            .Where(x => x.Status == "running" || x.Status == "starting")
            .ToListAsync(ct);
        foreach (var operation in interrupted)
        {
            operation.Status = operation.CancelRequested ? "cancelled" : "queued";
            operation.Phase = operation.CancelRequested ? "cancelled-after-restart" : "recovered-after-restart";
            operation.UpdatedAtUtc = DateTimeOffset.UtcNow;
        }
        if (interrupted.Count > 0)
        {
            await db.SaveChangesAsync(ct);
            logger.LogWarning("Recovered {Count} interrupted account lifecycle operations", interrupted.Count);
        }
    }
}
