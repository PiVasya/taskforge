using System.Collections.Concurrent;
using StackExchange.Redis;
using TaskForge.Browser.Api.Configuration;
using TaskForge.Browser.Api.Contracts;

namespace TaskForge.Browser.Api.Infrastructure;

public sealed class RedisFixedWindowRateLimiter(
    IServiceProvider services,
    BrowserRateLimitOptions options,
    ILogger<RedisFixedWindowRateLimiter> logger)
{
    private const string IncrementScript = """
        local count = redis.call('INCR', KEYS[1])
        if count == 1 then
          redis.call('EXPIRE', KEYS[1], ARGV[1])
        end
        local ttl = redis.call('TTL', KEYS[1])
        return { count, ttl }
        """;

    private sealed record LocalCounter(long Window, int Count);

    private readonly IConnectionMultiplexer? _redis = services.GetService<IConnectionMultiplexer>();
    private readonly BrowserRateLimitOptions _options = options;
    private readonly ILogger<RedisFixedWindowRateLimiter> _logger = logger;
    private readonly ConcurrentDictionary<string, LocalCounter> _local = new(StringComparer.Ordinal);

    public async Task<RateLimitDecision> CheckAsync(
        string bucket,
        string ownerKey,
        string networkKey,
        int limit,
        int windowSeconds,
        CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            return new RateLimitDecision(true, limit, limit, 0, bucket);
        }

        var owner = await IncrementAsync($"tf:browser:rl:{bucket}:owner:{ownerKey}", limit, windowSeconds, cancellationToken);
        if (!owner.Allowed) return owner with { Bucket = bucket };

        var networkLimit = checked(System.Math.Max(limit, limit * System.Math.Clamp(_options.NetworkMultiplier, 1, 100)));
        var network = await IncrementAsync($"tf:browser:rl:{bucket}:network:{networkKey}", networkLimit, windowSeconds, cancellationToken);
        if (!network.Allowed) return network with { Bucket = bucket };

        return owner with { Bucket = bucket };
    }

    private async Task<RateLimitDecision> IncrementAsync(string key, int limit, int windowSeconds, CancellationToken cancellationToken)
    {
        limit = System.Math.Clamp(limit, 1, 100000);
        windowSeconds = System.Math.Clamp(windowSeconds, 1, 86400);

        if (_redis is not null)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var db = _redis.GetDatabase();
                var result = await db.ScriptEvaluateAsync(
                    IncrementScript,
                    new RedisKey[] { key },
                    new RedisValue[] { windowSeconds });
                var values = (RedisResult[]?)result;
                if (values is null || values.Length < 2)
                {
                    throw new InvalidOperationException("Redis rate limiter script returned an invalid result.");
                }

                var count = (long)values[0];
                var ttlSeconds = (long)values[1];
                var retry = System.Math.Max(1, ttlSeconds > 0 ? (int)ttlSeconds : windowSeconds);
                var remaining = System.Math.Max(0, limit - (int)count);
                return new RateLimitDecision(count <= limit, limit, remaining, count <= limit ? 0 : retry, string.Empty);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Redis rate limiter failed; browser-api is using its local fallback.");
            }
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var window = now / windowSeconds;
        var localKey = $"{key}:{window}";
        var current = _local.AddOrUpdate(localKey, _ => new LocalCounter(window, 1), (_, old) => new LocalCounter(window, old.Count + 1));
        if (_local.Count > 10000)
        {
            foreach (var stale in _local.Where(x => x.Value.Window < window - 2).Take(1000)) _local.TryRemove(stale.Key, out _);
        }

        var localRetry = System.Math.Max(1, windowSeconds - (int)(now % windowSeconds));
        return new RateLimitDecision(current.Count <= limit, limit, System.Math.Max(0, limit - current.Count), current.Count <= limit ? 0 : localRetry, string.Empty);
    }
}
