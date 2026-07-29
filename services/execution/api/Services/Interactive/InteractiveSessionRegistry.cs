using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

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

internal sealed class InteractiveSessionRegistry
{
    private sealed record Entry(InteractiveSessionPayload Payload, string TicketHash, bool Active);

    private readonly ConcurrentDictionary<Guid, Entry> _sessions = new();
    private readonly object _gate = new();

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
        lock (_gate)
        {
            CleanupExpired();
            var limit = isAdmin ? 3 : 1;
            var current = _sessions.Values.Count(x => x.Payload.UserId == userId);
            if (current >= limit) return null;

            var id = Guid.NewGuid();
            var ticketBytes = RandomNumberGenerator.GetBytes(32);
            var ticket = Convert.ToHexString(ticketBytes).ToLowerInvariant();
            var payload = new InteractiveSessionPayload(
                id,
                userId,
                language,
                runnerService,
                code,
                columns,
                rows,
                timeLimitMs,
                memoryLimitMb,
                attestation.Clone(),
                DateTimeOffset.UtcNow.AddSeconds(75));
            _sessions[id] = new Entry(payload, HashTicket(ticket), Active: false);
            return (payload, ticket);
        }
    }

    internal bool TryActivate(Guid sessionId, string? ticket, out InteractiveSessionPayload payload)
    {
        payload = default!;
        if (string.IsNullOrWhiteSpace(ticket) || ticket.Length > 256) return false;
        lock (_gate)
        {
            CleanupExpired();
            if (!_sessions.TryGetValue(sessionId, out var entry) || entry.Active) return false;
            if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(entry.TicketHash),
                    Encoding.ASCII.GetBytes(HashTicket(ticket))))
            {
                return false;
            }
            var activePayload = entry.Payload with { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(3) };
            _sessions[sessionId] = entry with { Payload = activePayload, Active = true };
            payload = activePayload;
            return true;
        }
    }

    internal void Release(Guid sessionId) => _sessions.TryRemove(sessionId, out _);

    private void CleanupExpired()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var pair in _sessions)
        {
            if (pair.Value.Payload.ExpiresAt <= now)
            {
                _sessions.TryRemove(pair.Key, out _);
            }
        }
    }

    private static string HashTicket(string ticket) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ticket))).ToLowerInvariant();
}
