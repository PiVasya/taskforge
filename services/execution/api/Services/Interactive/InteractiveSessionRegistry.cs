using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using StackExchange.Redis;

namespace TaskForge.Execution.Api.Services.Interactive;

internal sealed record InteractiveSessionPayload(
    Guid Id,
    Guid UserId,
    string Language,
    string RunnerService,
    string Code,
    int Columns,
    int Rows,
    int TimeLimitMs,
    int MemoryLimitMb,
    JsonElement Attestation,
    DateTimeOffset ExpiresAt);

/// <summary>
/// Stores short-lived compiler start tickets. Compose keeps the original in-memory
/// behavior; MultiNode requires Redis so CREATE and WebSocket may reach any API pod.
/// Redis errors are fatal to ticket operations: never fall back to node-local state.
/// </summary>
internal sealed class InteractiveSessionRegistry
{
    private sealed record Entry(InteractiveSessionPayload Payload, string TicketHash, bool Active);

    private const int PendingSeconds = 75;
    private const int ActiveSeconds = 180;
    private const int SlotLifetimeMs = 240_000;

    // Atomic per-user reservation. The sorted set stores session IDs by expiry,
    // rather than a simple incrementing counter that would leak on node crashes.
    private const string ReserveScript = """
        redis.call('ZREMRANGEBYSCORE', KEYS[1], '-inf', ARGV[1])
        if redis.call('ZCARD', KEYS[1]) >= tonumber(ARGV[2]) then return 0 end
        local created = redis.call('SET', KEYS[2], ARGV[3], 'PX', ARGV[4], 'NX')
        if not created then return 0 end
        redis.call('ZADD', KEYS[1], ARGV[5], ARGV[6])
        redis.call('PEXPIRE', KEYS[1], ARGV[7])
        return 1
        """;

    // A valid ticket can be consumed only once, across all API replicas.
    // Both the session and the user's active-slot lease are changed atomically.
    private const string ActivateScript = """
        local raw = redis.call('GET', KEYS[1])
        if not raw then return nil end
        local entry = cjson.decode(raw)
        if entry.TicketHash ~= ARGV[1] then return nil end
        if entry.Payload.UserId ~= ARGV[5] then return nil end
        redis.call('DEL', KEYS[1])
        redis.call('ZADD', KEYS[2], ARGV[2], ARGV[3])
        redis.call('PEXPIRE', KEYS[2], ARGV[4])
        redis.call('SET', KEYS[3], ARGV[5], 'PX', ARGV[4])
        return raw
        """;

    private const string ReleaseScript = """
        local owner = redis.call('GET', KEYS[1])
        if not owner or owner ~= ARGV[2] then return 0 end
        redis.call('DEL', KEYS[1])
        redis.call('ZREM', KEYS[2], ARGV[1])
        return 1
        """;

    private readonly ConcurrentDictionary<Guid, Entry> _sessions = new();
    private readonly object _gate = new();
    private readonly IDatabase? _redis;

    public InteractiveSessionRegistry(IConfiguration configuration, IConnectionMultiplexer? redis = null)
    {
        var multiNode = configuration.GetValue("Cluster:MultiNode", false);
        var store = (configuration["InteractiveSessions:Store"] ?? (multiNode ? "redis" : "memory")).Trim();
        if (multiNode && !string.Equals(store, "redis", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Multi-node compiler sessions require InteractiveSessions:Store=redis.");
        if (string.Equals(store, "redis", StringComparison.OrdinalIgnoreCase))
        {
            _redis = redis?.GetDatabase() ?? throw new InvalidOperationException(
                "InteractiveSessions:Store=redis requires a configured Redis connection; refuse node-local fallback.");
        }
        else if (!string.Equals(store, "memory", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Unsupported InteractiveSessions:Store value; expected redis or memory.");
        }
    }

    internal (InteractiveSessionPayload Payload, string Ticket)? TryCreate(
        Guid userId,
        bool isAdmin,
        string language,
        string runnerService,
        string code,
        int columns,
        int rows,
        int timeLimitMs,
        int memoryLimitMb,
        JsonElement attestation)
    {
        var now = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid();
        var ticket = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var payload = new InteractiveSessionPayload(
            id, userId, language, runnerService, code, columns, rows,
            timeLimitMs, memoryLimitMb, attestation.Clone(), now.AddSeconds(PendingSeconds));
        var entry = new Entry(payload, HashTicket(ticket), Active: false);
        var limit = isAdmin ? 3 : 1;

        if (_redis is not null)
        {
            var expiryMs = payload.ExpiresAt.ToUnixTimeMilliseconds();
            var reserved = _redis.ScriptEvaluate(
                ReserveScript,
                new RedisKey[] { UserKey(userId), SessionKey(id) },
                new RedisValue[]
                {
                    now.ToUnixTimeMilliseconds(), limit, JsonSerializer.Serialize(entry),
                    PendingSeconds * 1000, expiryMs, id.ToString("N"), SlotLifetimeMs
                });
            return reserved.ToString() == "1" ? (payload, ticket) : null;
        }

        lock (_gate)
        {
            CleanupExpired();
            if (_sessions.Values.Count(x => x.Payload.UserId == userId) >= limit) return null;
            _sessions[id] = entry;
            return (payload, ticket);
        }
    }

    internal bool TryActivate(Guid sessionId, string? ticket, out InteractiveSessionPayload payload)
    {
        payload = default!;
        if (string.IsNullOrWhiteSpace(ticket) || ticket.Length > 256) return false;
        var hashedTicket = HashTicket(ticket);

        if (_redis is not null)
        {
            var serialized = _redis.StringGet(SessionKey(sessionId));
            if (serialized.IsNullOrEmpty) return false;
            var pending = JsonSerializer.Deserialize<Entry>(serialized.ToString());
            if (pending is null || pending.Payload.Id != sessionId || !TicketEquals(pending.TicketHash, hashedTicket))
                return false;

            var userId = pending.Payload.UserId;
            var now = DateTimeOffset.UtcNow;
            var claimed = _redis.ScriptEvaluate(
                ActivateScript,
                new RedisKey[] { SessionKey(sessionId), UserKey(userId), ActiveKey(sessionId) },
                new RedisValue[]
                {
                    hashedTicket, now.AddSeconds(ActiveSeconds).ToUnixTimeMilliseconds(),
                    sessionId.ToString("N"), SlotLifetimeMs, userId.ToString("D")
                });
            var claimedJson = claimed.ToString();
            if (string.IsNullOrEmpty(claimedJson)) return false;
            var active = JsonSerializer.Deserialize<Entry>(claimedJson);
            if (active is null || active.Payload.Id != sessionId) return false;
            payload = active.Payload with { ExpiresAt = now.AddSeconds(ActiveSeconds) };
            return true;
        }

        lock (_gate)
        {
            CleanupExpired();
            if (!_sessions.TryGetValue(sessionId, out var entry) || entry.Active) return false;
            if (!TicketEquals(entry.TicketHash, hashedTicket)) return false;
            var activePayload = entry.Payload with { ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(ActiveSeconds) };
            _sessions[sessionId] = entry with { Payload = activePayload, Active = true };
            payload = activePayload;
            return true;
        }
    }

    internal void Release(Guid sessionId)
    {
        if (_redis is not null)
        {
            var activeUser = _redis.StringGet(ActiveKey(sessionId));
            if (activeUser.IsNullOrEmpty || !Guid.TryParse(activeUser.ToString(), out var userId)) return;
            _redis.ScriptEvaluate(
                ReleaseScript,
                new RedisKey[] { ActiveKey(sessionId), UserKey(userId) },
                new RedisValue[] { sessionId.ToString("N"), userId.ToString("D") });
            return;
        }
        _sessions.TryRemove(sessionId, out _);
    }

    private void CleanupExpired()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var pair in _sessions)
            if (pair.Value.Payload.ExpiresAt <= now)
                _sessions.TryRemove(pair.Key, out _);
    }

    private static RedisKey SessionKey(Guid id) => $"tf:interactive:{{compiler}}:pending:{id:N}";
    private static RedisKey ActiveKey(Guid id) => $"tf:interactive:{{compiler}}:active:{id:N}";
    private static RedisKey UserKey(Guid id) => $"tf:interactive:{{compiler}}:user:{id:N}";

    private static bool TicketEquals(string expected, string actual)
    {
        if (expected.Length != actual.Length) return false;
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(actual));
    }

    private static string HashTicket(string ticket) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ticket))).ToLowerInvariant();
}
