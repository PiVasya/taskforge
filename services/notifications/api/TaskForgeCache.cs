using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

public static class TaskForgeCache
{
    private static readonly JsonSerializerOptions CacheJsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public static IServiceCollection AddTaskForgeRedisCache(this IServiceCollection services, IConfiguration configuration, string serviceName)
    {
        services.AddMemoryCache();

        var enabled = configuration.GetValue("Cache:Enabled", true);
        var connection = ResolveRedisConnection(configuration);
        if (enabled && !string.IsNullOrWhiteSpace(connection))
        {
            services.AddStackExchangeRedisCache(options =>
            {
                options.Configuration = connection;
                options.InstanceName = $"tf:{Sanitize(serviceName)}:";
            });
        }
        else
        {
            services.AddDistributedMemoryCache();
        }

        return services;
    }

    public static TimeSpan Ttl(IConfiguration configuration, string name, int fallbackSeconds)
    {
        var specific = configuration.GetValue<int?>($"Cache:{name}TtlSeconds");
        var generic = configuration.GetValue<int?>("Cache:DefaultTtlSeconds");
        var seconds = specific ?? generic ?? fallbackSeconds;
        return TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 86400));
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
                    logger?.LogDebug("TF cache hit {CacheKey}", key);
                    return value;
                }
            }
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "TF cache read failed for {CacheKey}; falling back to source", key);
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
            logger?.LogDebug("TF cache set {CacheKey} ttl={CacheTtlSeconds}s", key, ttl.TotalSeconds);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "TF cache write failed for {CacheKey}", key);
        }

        return fresh;
    }

    private static string? ResolveRedisConnection(IConfiguration configuration)
        => configuration.GetConnectionString("Redis")
           ?? configuration["Redis:ConnectionString"]
           ?? configuration["Cache:RedisConnection"]
           ?? Environment.GetEnvironmentVariable("REDIS_CONNECTION");

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
