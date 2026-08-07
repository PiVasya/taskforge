using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using StackExchange.Redis;

namespace TaskForge.Identity.Api.Services.Security;

public sealed record IdentityRateLimitDecision(bool Allowed, int Limit, int Remaining, int RetryAfterSeconds);

public sealed class IdentityAuthRateLimiter(IServiceProvider services, ILogger<IdentityAuthRateLimiter> logger)
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
    private readonly ILogger<IdentityAuthRateLimiter> _logger = logger;
    private readonly ConcurrentDictionary<string, LocalCounter> _local = new(StringComparer.Ordinal);

    public async Task<IdentityRateLimitDecision> CheckAsync(
        HttpContext http,
        string bucket,
        string? identity,
        CancellationToken cancellationToken)
    {
        var (limit, window) = ResolveRule(bucket);
        var network = Hash(ResolveNetworkAddress(http));
        var normalizedIdentity = NormalizeIdentity(identity);
        var identityKey = Hash(normalizedIdentity);
        var ownerKey = $"tf:identity:rl:{bucket}:owner:{network}:{identityKey}";
        var networkKey = $"tf:identity:rl:{bucket}:network:{network}";

        var owner = await IncrementAsync(ownerKey, limit, window, cancellationToken);
        if (!owner.Allowed) return owner;

        var networkRule = ResolveNetworkRule(bucket, limit, window);
        var networkDecision = await IncrementAsync(networkKey, networkRule.Limit, networkRule.Window, cancellationToken);
        if (!networkDecision.Allowed) return networkDecision;

        var secondaryRule = ResolveSecondaryNetworkRule(bucket);
        if (secondaryRule is not null)
        {
            var secondaryKey = $"tf:identity:rl:{bucket}:network:{secondaryRule.Value.Suffix}:{network}";
            var secondaryDecision = await IncrementAsync(
                secondaryKey,
                secondaryRule.Value.Limit,
                secondaryRule.Value.Window,
                cancellationToken);
            if (!secondaryDecision.Allowed) return secondaryDecision;
        }

        return owner;
    }

    private async Task<IdentityRateLimitDecision> IncrementAsync(string key, int limit, TimeSpan window, CancellationToken cancellationToken)
    {
        if (_redis is not null)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var db = _redis.GetDatabase();
                var windowSeconds = System.Math.Max(1, (int)window.TotalSeconds);
                var result = await db.ScriptEvaluateAsync(
                    IncrementScript,
                    new RedisKey[] { key },
                    new RedisValue[] { windowSeconds });
                var values = (RedisResult[])result;
                var count = (long)values[0];
                var ttlSeconds = (long)values[1];
                var retryAfter = System.Math.Max(1, ttlSeconds > 0 ? (int)ttlSeconds : windowSeconds);
                return new IdentityRateLimitDecision(count <= limit, limit, System.Math.Max(0, limit - (int)count), count <= limit ? 0 : retryAfter);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Redis auth rate limiter failed; identity-api is using its process-local safety fallback.");
            }
        }

        var seconds = System.Math.Max(1, (int)window.TotalSeconds);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var windowId = now / seconds;
        var localKey = $"{key}:{windowId}";
        var counter = _local.AddOrUpdate(localKey, _ => new LocalCounter(windowId, 1), (_, old) => new LocalCounter(windowId, old.Count + 1));
        if (_local.Count > 20000)
        {
            foreach (var stale in _local.Where(x => x.Value.Window < windowId - 2).Take(2000)) _local.TryRemove(stale.Key, out _);
        }

        var retry = System.Math.Max(1, seconds - (int)(now % seconds));
        return new IdentityRateLimitDecision(counter.Count <= limit, limit, System.Math.Max(0, limit - counter.Count), counter.Count <= limit ? 0 : retry);
    }

    private static (int Limit, TimeSpan Window) ResolveRule(string bucket) => bucket switch
    {
        "login" => (12, TimeSpan.FromMinutes(5)),
        "register" => (5, TimeSpan.FromMinutes(10)),
        "refresh" => (120, TimeSpan.FromMinutes(5)),
        "password" => (8, TimeSpan.FromMinutes(10)),
        "password-recovery-request" => (6, TimeSpan.FromMinutes(15)),
        "password-recovery-verify" => (12, TimeSpan.FromMinutes(10)),
        "password-recovery-reset" => (6, TimeSpan.FromMinutes(10)),
        _ => (60, TimeSpan.FromMinutes(1))
    };

    private static (int Limit, TimeSpan Window) ResolveNetworkRule(string bucket, int ownerLimit, TimeSpan ownerWindow)
        => bucket switch
        {
            "register" => (20, TimeSpan.FromHours(1)),
            _ => (checked(ownerLimit * 10), ownerWindow)
        };

    private static (int Limit, TimeSpan Window, string Suffix)? ResolveSecondaryNetworkRule(string bucket)
        => bucket switch
        {
            "register" => (80, TimeSpan.FromDays(1), "daily"),
            _ => null
        };

    private static string ResolveNetworkAddress(HttpContext http)
    {
        foreach (var value in new[]
                 {
                     http.Request.Headers["X-TaskForge-Client-IP"].FirstOrDefault(),
                     http.Request.Headers["X-Real-IP"].FirstOrDefault(),
                     http.Connection.RemoteIpAddress?.ToString()
                 })
        {
            if (IPAddress.TryParse(value, out var address)) return address.MapToIPv6().ToString();
        }
        return "unknown";
    }


    private static string NormalizeIdentity(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "none";
        var normalized = value.Trim().ToLowerInvariant();
        return normalized[..System.Math.Min(normalized.Length, 320)];
    }

    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant()[..24];
}
