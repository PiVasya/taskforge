using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using TaskForge.Tasks.Api.Data;
using TaskForge.Tasks.Api.Domain;


namespace TaskForge.Tasks.Api.Contracts;

public sealed record LoadedImageBytes(byte[] Bytes, string? ContentType, string? FileName);

internal static class TaskForgeApiRateLimiters
{
    private static readonly SlidingWindowRateLimiter Limiter = new();
    public static bool Allow(string bucket, string key)
    {
        var (limit, window) = bucket switch
        {
            "task-submit" => (40, TimeSpan.FromMinutes(5)),
            "image-test" => (20, TimeSpan.FromMinutes(5)),
            _ => (60, TimeSpan.FromMinutes(1))
        };
        return Limiter.Allow(key, limit, window);
    }
}

internal sealed class SlidingWindowRateLimiter
{
    private readonly ConcurrentDictionary<string, Queue<long>> _hits = new();
    public bool Allow(string key, int limit, TimeSpan window)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var min = now - (long)window.TotalMilliseconds;
        var queue = _hits.GetOrAdd(key, _ => new Queue<long>());
        lock (queue)
        {
            while (queue.Count > 0 && queue.Peek() < min) queue.Dequeue();
            if (queue.Count >= limit) return false;
            queue.Enqueue(now);
            return true;
        }
    }
}
