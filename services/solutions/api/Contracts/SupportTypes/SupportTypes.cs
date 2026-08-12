using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using TaskForge.Solutions.Api.Data;
using TaskForge.Solutions.Api.Domain;


namespace TaskForge.Solutions.Api.Contracts;

public sealed record LeaderboardActivityRow(Guid UserId, Guid AssignmentId, int Rating, DateTimeOffset SubmittedAt, string Kind);

internal static class TaskForgeApiRateLimiters
{
    private static readonly SlidingWindowRateLimiter Limiter = new();
    public static bool Allow(string bucket, string key, int multiplier = 1)
    {
        var (limit, window) = bucket switch
        {
            "solution-submit" => (30, TimeSpan.FromMinutes(5)),
            _ => (60, TimeSpan.FromMinutes(1))
        };
        var effectiveMultiplier = System.Math.Clamp(multiplier, 1, 100);
        var effectiveLimit = System.Math.Min(100_000, limit * effectiveMultiplier);
        return Limiter.Allow(key, effectiveLimit, window);
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
