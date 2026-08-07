using Microsoft.Extensions.Caching.Distributed;

namespace TaskForge.Browser.Api.Infrastructure;

public sealed class BrowserResponseCache(IDistributedCache cache, ILogger<BrowserResponseCache> logger)
{
    private readonly IDistributedCache _cache = cache;
    private readonly ILogger<BrowserResponseCache> _logger = logger;

    public async Task<byte[]?> GetAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            return await _cache.GetAsync(key, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Browser response cache read failed for {CacheKey}.", key);
            return null;
        }
    }

    public async Task SetAsync(string key, byte[] value, TimeSpan ttl, int maxBytes, CancellationToken cancellationToken)
    {
        if (value.Length == 0 || value.Length > maxBytes || ttl <= TimeSpan.Zero) return;
        try
        {
            await _cache.SetAsync(key, value, new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = ttl
            }, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Browser response cache write failed for {CacheKey}.", key);
        }
    }
}
