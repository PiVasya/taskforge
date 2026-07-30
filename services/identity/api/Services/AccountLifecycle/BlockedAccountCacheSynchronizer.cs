using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using StackExchange.Redis;
using TaskForge.Identity.Api.Data;

namespace TaskForge.Identity.Api.Services.AccountLifecycle;

internal sealed class BlockedAccountCacheSynchronizer(
    IServiceScopeFactory scopeFactory,
    ILogger<BlockedAccountCacheSynchronizer> logger) : BackgroundService
{
    internal static string CacheKey(Guid userId) => $"tf:auth:blocked:{userId:N}";

    internal static async Task SetBlockedAsync(IServiceProvider services, Guid userId, string status, CancellationToken ct)
    {
        var value = string.IsNullOrWhiteSpace(status) ? "blocked" : status;
        var cache = services.GetService<IDistributedCache>();
        if (cache != null)
            await cache.SetStringAsync(CacheKey(userId), value, new DistributedCacheEntryOptions(), ct);

        var redis = services.GetService<IConnectionMultiplexer>();
        if (redis != null)
            await redis.GetDatabase().StringSetAsync(CacheKey(userId), value);
    }

    internal static async Task RemoveBlockedAsync(IServiceProvider services, Guid userId, CancellationToken ct)
    {
        var cache = services.GetService<IDistributedCache>();
        if (cache != null)
            await cache.RemoveAsync(CacheKey(userId), ct);

        var redis = services.GetService<IConnectionMultiplexer>();
        if (redis != null)
            await redis.GetDatabase().KeyDeleteAsync(CacheKey(userId));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SynchronizeAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Unable to synchronize blocked account cache");
            }

            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }

    private async Task SynchronizeAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        var services = scope.ServiceProvider;
        var now = DateTimeOffset.UtcNow;

        var blocked = await db.BlockedAccounts.AsNoTracking()
            .Where(x => !x.ExpiresAtUtc.HasValue || x.ExpiresAtUtc > now)
            .Select(x => x.UserId)
            .ToListAsync(ct);
        var expired = await (from block in db.BlockedAccounts.AsNoTracking()
                             join user in db.Users.AsNoTracking() on block.UserId equals user.Id
                             where block.ExpiresAtUtc.HasValue && block.ExpiresAtUtc <= now && user.AccountStatus == "active"
                             select block.UserId)
            .ToListAsync(ct);
        var unavailable = await db.Users.AsNoTracking()
            .Where(x => x.AccountStatus != "active")
            .Select(x => new { x.Id, x.AccountStatus })
            .ToListAsync(ct);

        foreach (var userId in expired)
            await RemoveBlockedAsync(services, userId, ct);
        foreach (var userId in blocked)
            await SetBlockedAsync(services, userId, "blocked", ct);
        foreach (var user in unavailable)
            await SetBlockedAsync(services, user.Id, user.AccountStatus, ct);
    }
}
