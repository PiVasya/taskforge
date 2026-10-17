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
    // Once the table is full, unknown identities are rejected, not admitted
    // without rate limiting. Existing identities retain their counters.
    private const int DefaultMaxKeys = 8192;
    private readonly object _sync = new();
    private sealed class WindowHits(long durationMilliseconds)
    {
        internal readonly Queue<long> Times = new();
        internal long DurationMilliseconds = durationMilliseconds;
    }

    private readonly Dictionary<string, WindowHits> _hits = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;
    private readonly int _maxKeys;
    private long _nextFullSweepAt;

    internal SlidingWindowRateLimiter(int maxKeys = DefaultMaxKeys, TimeProvider? time = null)
    {
        if (maxKeys < 1) throw new ArgumentOutOfRangeException(nameof(maxKeys));
        _maxKeys = maxKeys;
        _time = time ?? TimeProvider.System;
    }

    public bool Allow(string key, int limit, TimeSpan window)
    {
        if (string.IsNullOrEmpty(key) || key.Length > 512 || limit <= 0 || window <= TimeSpan.Zero)
            return false;

        var now = _time.GetUtcNow().ToUnixTimeMilliseconds();
        var min = now - (long)window.TotalMilliseconds;
        lock (_sync)
        {
            if (!_hits.TryGetValue(key, out var queue))
            {
                if (_hits.Count >= _maxKeys)
                {
                    // Sweep at most once per second even during sustained abuse.
                    if (now >= _nextFullSweepAt)
                    {
                        foreach (var entry in _hits.ToArray())
                        {
                            var oldestAllowed = now - entry.Value.DurationMilliseconds;
                            while (entry.Value.Times.Count > 0 && entry.Value.Times.Peek() < oldestAllowed)
                                entry.Value.Times.Dequeue();
                            if (entry.Value.Times.Count == 0) _hits.Remove(entry.Key);
                        }
                        _nextFullSweepAt = now + 1000;
                    }
                    if (_hits.Count >= _maxKeys) return false;
                }
                queue = new WindowHits((long)window.TotalMilliseconds);
                _hits.Add(key, queue);
            }

            queue.DurationMilliseconds = (long)window.TotalMilliseconds;
            while (queue.Times.Count > 0 && queue.Times.Peek() < min) queue.Times.Dequeue();
            if (queue.Times.Count >= limit) return false;
            queue.Times.Enqueue(now);
            return true;
        }
    }
}
