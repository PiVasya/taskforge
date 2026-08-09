using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using TaskForge.Browser.Api.Configuration;
using TaskForge.Browser.Api.Contracts;
using TaskForge.Browser.Api.Security;

namespace TaskForge.Browser.Api.Services;

internal sealed partial class AiRemoteBrowserSessionStore(AiRemoteBrowserOptions options)
{
    private readonly AiRemoteBrowserOptions _options = options;
    private readonly ConcurrentDictionary<string, AiRemoteStartChallenge> _challenges = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, AiRemoteBrowserSession> _sessions = new();

    public (string Challenge, AiRemoteStartChallenge Record) CreateChallenge(
        string site,
        string path,
        int width,
        int height,
        int waitMs,
        string provider,
        string networkKey)
    {
        CleanupChallenges();
        var now = DateTimeOffset.UtcNow;
        var record = new AiRemoteStartChallenge(
            site,
            path,
            width,
            height,
            waitMs,
            provider,
            networkKey,
            now,
            now.AddSeconds(Math.Clamp(_options.StartChallengeTtlSeconds, 30, 1800)));

        for (var i = 0; i < 4; i++)
        {
            var raw = RandomSecret(24);
            if (_challenges.TryAdd(Hash(raw), record)) return (raw, record);
        }

        throw new BrowserApiException(
            StatusCodes.Status503ServiceUnavailable,
            "AI_REMOTE_START_FAILED",
            "Не удалось создать одноразовый remote-browser challenge. Повторите запрос.",
            retryAfterSeconds: 1);
    }

    public AiRemoteStartChallenge ConsumeChallenge(string? challenge)
    {
        ValidateSecret(challenge, "AI_REMOTE_CHALLENGE_INVALID");
        var key = Hash(challenge!);
        if (!_challenges.TryRemove(key, out var record) || record.ExpiresAtUtc <= DateTimeOffset.UtcNow)
        {
            throw new BrowserApiException(
                StatusCodes.Status410Gone,
                "AI_REMOTE_CHALLENGE_EXPIRED",
                "Одноразовая ссылка запуска истекла или уже использована. Откройте /api/ai/browser/start повторно.");
        }
        return record;
    }


    public void EnsureNetworkCapacity(string networkKey)
    {
        var now = DateTimeOffset.UtcNow;
        var activeForNetwork = _sessions.Values.Count(x => !x.IsExpired(now) && string.Equals(x.NetworkKey, networkKey, StringComparison.Ordinal));
        if (activeForNetwork >= Math.Clamp(_options.MaxSessionsPerNetwork, 1, 10))
        {
            throw new BrowserApiException(
                StatusCodes.Status429TooManyRequests,
                "AI_REMOTE_NETWORK_SESSION_LIMIT",
                "Для этого сетевого источника уже открыто максимальное число AI remote-browser сессий.",
                retryAfterSeconds: 10);
        }
    }

    public (AiRemoteBrowserSession Session, string Secret) AddSession(
        Guid browserSessionId,
        string browserSessionToken,
        BrowserCaller caller,
        AiRemoteStartChallenge challenge)
    {
        CleanupChallenges();
        var now = DateTimeOffset.UtcNow;
        EnsureNetworkCapacity(challenge.NetworkKey);

        for (var i = 0; i < 4; i++)
        {
            var id = Guid.NewGuid();
            var secret = RandomSecret(32);
            var session = new AiRemoteBrowserSession
            {
                Id = id,
                SecretHash = Hash(secret),
                Provider = challenge.Provider,
                NetworkKey = challenge.NetworkKey,
                Caller = caller,
                BrowserSessionId = browserSessionId,
                BrowserSessionToken = browserSessionToken,
                Site = challenge.Site,
                Width = challenge.Width,
                Height = challenge.Height,
                CreatedAtUtc = now,
                LastSeenAtUtc = now,
                AbsoluteExpiresAtUtc = now.AddMinutes(Math.Clamp(_options.SessionAbsoluteMinutes, 5, 240)),
                IdleTimeout = TimeSpan.FromMinutes(Math.Clamp(_options.SessionIdleMinutes, 5, 120))
            };
            if (_sessions.TryAdd(id, session)) return (session, secret);
        }

        throw new BrowserApiException(StatusCodes.Status500InternalServerError, "AI_REMOTE_SESSION_CREATE_FAILED", "Не удалось зарегистрировать AI remote-browser сессию.");
    }

    public AiRemoteBrowserSession Resolve(Guid id, string? secret, bool touch = true)
    {
        ValidateSecret(secret, "AI_REMOTE_SECRET_INVALID");
        if (!_sessions.TryGetValue(id, out var session))
        {
            throw new BrowserApiException(StatusCodes.Status404NotFound, "AI_REMOTE_SESSION_NOT_FOUND", "AI remote-browser сессия не найдена.");
        }

        var now = DateTimeOffset.UtcNow;
        if (session.IsExpired(now))
        {
            _sessions.TryRemove(id, out _);
            throw new BrowserApiException(StatusCodes.Status410Gone, "AI_REMOTE_SESSION_EXPIRED", "AI remote-browser сессия истекла. Создайте новую.");
        }

        if (!FixedEquals(session.SecretHash, Hash(secret!)))
        {
            throw new BrowserApiException(StatusCodes.Status401Unauthorized, "AI_REMOTE_SECRET_INVALID", "Неверный секрет AI remote-browser сессии.");
        }

        if (touch) session.LastSeenAtUtc = now;
        return session;
    }

    public bool TryRemove(Guid id, out AiRemoteBrowserSession? session)
        => _sessions.TryRemove(id, out session);

    public IReadOnlyList<AiRemoteBrowserSession> TakeExpired()
    {
        var now = DateTimeOffset.UtcNow;
        var result = new List<AiRemoteBrowserSession>();
        foreach (var pair in _sessions)
        {
            if (!pair.Value.IsExpired(now)) continue;
            if (_sessions.TryRemove(pair.Key, out var removed)) result.Add(removed);
        }
        CleanupChallenges();
        return result;
    }

    public int ActiveCount => _sessions.Values.Count(x => !x.IsExpired(DateTimeOffset.UtcNow));

    private void CleanupChallenges()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var pair in _challenges)
        {
            if (pair.Value.ExpiresAtUtc <= now) _challenges.TryRemove(pair.Key, out _);
        }
    }

    private static void ValidateSecret(string? value, string code)
    {
        if (value is null || !SecretPattern().IsMatch(value))
        {
            throw new BrowserApiException(StatusCodes.Status401Unauthorized, code, "Не указан или некорректен секрет AI remote-browser.");
        }
    }

    private static string RandomSecret(int bytes)
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static bool FixedEquals(string a, string b)
    {
        var aa = Encoding.UTF8.GetBytes(a);
        var bb = Encoding.UTF8.GetBytes(b);
        return aa.Length == bb.Length && CryptographicOperations.FixedTimeEquals(aa, bb);
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{20,100}$", RegexOptions.CultureInvariant)]
    private static partial Regex SecretPattern();
}
