using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

public static class TaskForgeCache
{
    private static readonly JsonSerializerOptions CacheJsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private static bool DebugLogsEnabled =>
        string.Equals(Environment.GetEnvironmentVariable("TASKFORGE_DEBUG_LOGS"), "1", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Environment.GetEnvironmentVariable("TASKFORGE_DEBUG_LOGS"), "true", StringComparison.OrdinalIgnoreCase);

    public static IServiceCollection AddTaskForgeRedisCache(this IServiceCollection services, IConfiguration configuration, string serviceName)
    {
        services.AddMemoryCache();

        var enabled = configuration.GetValue("Cache:Enabled", true);
        var connection = ResolveRedisConnection(configuration);
        var hasConnection = !string.IsNullOrWhiteSpace(connection);
        var instanceName = $"tf:{Sanitize(serviceName)}:";

        if (enabled && hasConnection)
        {
            services.AddStackExchangeRedisCache(options =>
            {
                options.Configuration = connection;
                options.InstanceName = instanceName;
            });
            services.AddSingleton<IConnectionMultiplexer>(_ =>
            {
                var options = ConfigurationOptions.Parse(connection!);
                options.AbortOnConnectFail = false;
                return ConnectionMultiplexer.Connect(options);
            });

            DebugCacheBoot(serviceName, "redis", enabled, hasConnection, instanceName);
        }
        else
        {
            services.AddDistributedMemoryCache();
            DebugCacheBoot(serviceName, enabled ? "memory" : "disabled-memory-fallback", enabled, hasConnection, instanceName);
        }

        return services;
    }

    public static TimeSpan Ttl(IConfiguration configuration, string name, int fallbackSeconds)
    {
        var specific = configuration.GetValue<int?>($"Cache:{name}TtlSeconds");
        var generic = configuration.GetValue<int?>("Cache:DefaultTtlSeconds");
        var seconds = specific ?? generic ?? fallbackSeconds;
        return TimeSpan.FromSeconds(System.Math.Clamp(seconds, 1, 86400));
    }

    public static string Key(string prefix, params object?[] parts)
        => $"tf:{Sanitize(prefix)}:{Hash(string.Join('|', parts.Select(NormalizePart)))}";

    public static async Task<T> GetOrSetAsync<T>(
        IDistributedCache cache,
        IConfiguration configuration,
        ILogger? logger,
        string key,
        TimeSpan ttl,
        Func<CancellationToken, Task<T>> factory,
        CancellationToken ct)
    {
        if (!configuration.GetValue("Cache:Enabled", true))
        {
            DebugCache("BYPASS", key, ttl, "cache_disabled");
            logger?.LogInformation("TFDBG CACHE BYPASS key={CacheKey} reason=cache_disabled", key);
            return await factory(ct);
        }

        try
        {
            var cached = await cache.GetStringAsync(key, ct);
            if (!string.IsNullOrWhiteSpace(cached))
            {
                var value = JsonSerializer.Deserialize<T>(cached, CacheJsonOptions);
                if (value is not null)
                {
                    DebugCache("HIT", key, ttl, $"bytes={cached.Length}");
                    logger?.LogInformation("TFDBG CACHE HIT key={CacheKey} bytes={CacheBytes}", key, cached.Length);
                    return value;
                }

                DebugCache("BAD", key, ttl, "deserialize_null");
                logger?.LogWarning("TFDBG CACHE BAD key={CacheKey} reason=deserialize_null", key);
            }
            else
            {
                DebugCache("MISS", key, ttl, "empty");
                logger?.LogInformation("TFDBG CACHE MISS key={CacheKey}", key);
            }
        }
        catch (Exception ex)
        {
            DebugCache("READ-FAIL", key, ttl, ex.GetType().Name);
            logger?.LogWarning(ex, "TFDBG CACHE READ-FAIL key={CacheKey}; falling back to source", key);
        }

        var fresh = await factory(ct);
        try
        {
            var payload = JsonSerializer.Serialize(fresh, CacheJsonOptions);
            await cache.SetStringAsync(
                key,
                payload,
                new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = ttl },
                ct);
            DebugCache("SET", key, ttl, $"bytes={payload.Length}");
            logger?.LogInformation("TFDBG CACHE SET key={CacheKey} ttl={CacheTtlSeconds}s bytes={CacheBytes}", key, ttl.TotalSeconds, payload.Length);
        }
        catch (Exception ex)
        {
            DebugCache("WRITE-FAIL", key, ttl, ex.GetType().Name);
            logger?.LogWarning(ex, "TFDBG CACHE WRITE-FAIL key={CacheKey}", key);
        }

        return fresh;
    }

    private static string? ResolveRedisConnection(IConfiguration configuration)
        => configuration.GetConnectionString("Redis")
           ?? configuration["Redis:ConnectionString"]
           ?? configuration["Cache:RedisConnection"]
           ?? Environment.GetEnvironmentVariable("REDIS_CONNECTION");

    private static void DebugCacheBoot(string serviceName, string provider, bool enabled, bool hasConnection, string instanceName)
    {
        if (!DebugLogsEnabled) return;
        Console.WriteLine($"[TFDBG CACHE BOOT] service={Sanitize(serviceName)} enabled={enabled.ToString().ToLowerInvariant()} provider={provider} redisConfigured={hasConnection.ToString().ToLowerInvariant()} instance={instanceName} utc={DateTimeOffset.UtcNow:O}");
    }

    private static void DebugCache(string action, string key, TimeSpan ttl, string details)
    {
        if (!DebugLogsEnabled) return;
        Console.WriteLine($"[TFDBG CACHE {action}] key={key} ttl={System.Math.Round(ttl.TotalSeconds)}s details={details} utc={DateTimeOffset.UtcNow:O}");
    }

    private static string NormalizePart(object? value)
    {
        if (value is null) return "null";
        if (value is IEnumerable<Guid> ids) return string.Join(',', ids.OrderBy(x => x).Select(x => x.ToString("N")));
        if (value is IEnumerable<string> strings) return string.Join(',', strings.OrderBy(x => x, StringComparer.Ordinal));
        if (value is DateTimeOffset dto) return dto.ToUnixTimeSeconds().ToString();
        return Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static string Hash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant()[..24];
    }

    private static string Sanitize(string value)
    {
        var raw = string.IsNullOrWhiteSpace(value) ? "default" : value.Trim().ToLowerInvariant();
        var chars = raw.Select(ch => char.IsLetterOrDigit(ch) ? ch : ':').ToArray();
        return string.Join(':', new string(chars).Split(':', StringSplitOptions.RemoveEmptyEntries));
    }
}
