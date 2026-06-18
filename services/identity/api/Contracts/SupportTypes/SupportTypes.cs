using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.IdentityModel.Tokens;
using TaskForge.Identity.Api.Data;
using TaskForge.Identity.Api.Domain;


namespace TaskForge.Identity.Api.Contracts;

public sealed record PublicProfileExtra(
    bool PublicProfileEnabled,
    string? Bio,
    string? Location,
    string? Education,
    string? Github,
    string? Telegram,
    string? Website,
    IReadOnlyList<string> Skills,
    bool ShowInLeaderboard,
    bool ShowBio,
    bool ShowLocation,
    bool ShowEducation,
    bool ShowGithub,
    bool ShowTelegram,
    bool ShowWebsite,
    bool ShowSkills,
    bool ShowStats)
{
    public static PublicProfileExtra Empty { get; } = new(true, null, null, null, null, null, null, Array.Empty<string>(), true, false, false, false, false, false, false, false, false);
}

internal static class TaskForgeAuthRateLimiters
{
    private static readonly SlidingWindowRateLimiter Limiter = new();

    public static bool Allow(string bucket, string key)
    {
        var (limit, window) = bucket switch
        {
            "login" => (12, TimeSpan.FromMinutes(5)),
            "register" => (5, TimeSpan.FromMinutes(10)),
            "refresh" => (120, TimeSpan.FromMinutes(5)),
            "password" => (8, TimeSpan.FromMinutes(10)),
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
