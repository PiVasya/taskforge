using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace TaskForge.Cluster;

// This is application wiring, not a node topology or load-balancer.
// Cluster deployment provides Redis and its own namespace. Compose keeps the
// original in-process SignalR behavior when Cluster:MultiNode is unset.
public static class DistributedSignalR
{
    public static ISignalRServerBuilder AddTaskForgeSignalR(
        this IServiceCollection services, IConfiguration configuration, string serviceName)
    {
        var builder = services.AddSignalR();
        if (!configuration.GetValue("Cluster:MultiNode", false)) return builder;

        var ns = configuration["Cluster:Namespace"]?.Trim();
        if (string.IsNullOrWhiteSpace(ns) || ns.Length > 64 ||
            ns.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch is not ('-' or '_')))
            throw new InvalidOperationException(
                "Multi-node SignalR requires Cluster__Namespace (1-64 ASCII letters, digits, '-' or '_') to isolate traffic between environments.");

        var redis = configuration.GetConnectionString("Redis")
            ?? configuration["Redis:ConnectionString"]
            ?? configuration["Cache:RedisConnection"]
            ?? Environment.GetEnvironmentVariable("REDIS_CONNECTION");
        if (string.IsNullOrWhiteSpace(redis))
            throw new InvalidOperationException(
                "Multi-node SignalR requires a shared Redis connection; local-only delivery is unsafe behind a load balancer.");

        builder.AddStackExchangeRedis(redis, options =>
        {
            options.Configuration.ChannelPrefix = RedisChannel.Literal($"tf:{ns}:signalr:{serviceName}");
        });
        return builder;
    }
}
