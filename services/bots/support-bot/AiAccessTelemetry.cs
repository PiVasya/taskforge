using System.Net;

namespace TaskForge.SupportBot;

public sealed record AiAccessEventBatch(IReadOnlyList<AiAccessEventDto> Events);

public sealed record AiAccessEventDto(
    DateTimeOffset AtUtc,
    string Source,
    string Category,
    string Operation,
    string Method,
    int StatusCode,
    long DurationMs,
    string VisitorKey,
    string? ClientIp,
    bool IsAuthenticated,
    string AccountType,
    Guid? UserId,
    string? AccountName,
    string? UserAgent,
    string? TargetSite,
    string? TargetPath,
    string? ReferrerHost,
    string? TraceId);

public sealed record AiAccessDigestLease(int Count, IReadOnlyList<AiAccessEventDto> Events);

public sealed class AiAccessDigestAggregator(IConfiguration cfg, ILogger<AiAccessDigestAggregator> logger)
{
    private readonly object _gate = new();
    private readonly List<AiAccessEventDto> _events = new();
    private DateTimeOffset _lastSentUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _lastAttemptUtc = DateTimeOffset.MinValue;

    public int PendingCount
    {
        get
        {
            lock (_gate) return _events.Count;
        }
    }

    public void Record(IEnumerable<AiAccessEventDto> incoming)
    {
        if (!cfg.GetValue("AiAccessTelemetry:Enabled", true)) return;

        var accepted = incoming
            .Where(IsSane)
            .Take(100)
            .Select(Sanitize)
            .ToArray();
        if (accepted.Length == 0) return;

        lock (_gate)
        {
            CleanupExpired(DateTimeOffset.UtcNow);
            _events.AddRange(accepted);

            var maxEvents = Math.Clamp(cfg.GetValue("AiAccessTelemetry:MaxPendingEvents", 2000), 100, 10000);
            if (_events.Count > maxEvents)
            {
                var remove = _events.Count - maxEvents;
                _events.RemoveRange(0, remove);
                logger.LogWarning("AI access telemetry buffer dropped {Count} oldest events because the in-memory limit was reached.", remove);
            }
        }
    }

    public AiAccessDigestLease? TryPrepareDigest(DateTimeOffset nowUtc)
    {
        lock (_gate)
        {
            CleanupExpired(nowUtc);
            if (_events.Count == 0) return null;

            var minInterval = TimeSpan.FromSeconds(Math.Clamp(cfg.GetValue("AiAccessTelemetry:MinNotificationIntervalSeconds", 120), 30, 3600));
            if (_lastSentUtc != DateTimeOffset.MinValue && nowUtc - _lastSentUtc < minInterval) return null;

            // Failed Telegram sends are retried, but never on every support-bot poll tick.
            if (_lastAttemptUtc != DateTimeOffset.MinValue && nowUtc - _lastAttemptUtc < TimeSpan.FromSeconds(20)) return null;

            var first = _events.Min(x => x.AtUtc);
            var last = _events.Max(x => x.AtUtc);
            var age = nowUtc - first;
            var quiet = nowUtc - last;
            var hasError = _events.Any(x => x.StatusCode >= 400);
            var hasMeaningfulUse = _events.Any(IsMeaningfulUse);
            var discoveryMin = Math.Clamp(cfg.GetValue("AiAccessTelemetry:DiscoveryOnlyMinEvents", 4), 2, 50);
            var quietSeconds = Math.Clamp(cfg.GetValue("AiAccessTelemetry:QuietSeconds", 45), 10, 600);
            var maxDigestSeconds = Math.Clamp(cfg.GetValue("AiAccessTelemetry:MaxDigestIntervalSeconds", 300), 60, 3600);

            var due = hasError
                ? quiet >= TimeSpan.FromSeconds(Math.Min(20, quietSeconds)) || age >= TimeSpan.FromSeconds(60)
                : hasMeaningfulUse
                    ? quiet >= TimeSpan.FromSeconds(quietSeconds) || age >= TimeSpan.FromSeconds(maxDigestSeconds)
                    : _events.Count >= discoveryMin &&
                      (quiet >= TimeSpan.FromSeconds(Math.Max(60, quietSeconds)) || age >= TimeSpan.FromSeconds(maxDigestSeconds));

            if (!due) return null;

            var maxPerDigest = Math.Clamp(cfg.GetValue("AiAccessTelemetry:MaxEventsPerDigest", 500), 25, 2000);
            var count = Math.Min(_events.Count, maxPerDigest);
            _lastAttemptUtc = nowUtc;
            return new AiAccessDigestLease(count, _events.Take(count).ToArray());
        }
    }

    public void MarkSent(AiAccessDigestLease lease, DateTimeOffset nowUtc)
    {
        lock (_gate)
        {
            var count = Math.Min(lease.Count, _events.Count);
            if (count > 0) _events.RemoveRange(0, count);
            _lastSentUtc = nowUtc;
            _lastAttemptUtc = nowUtc;
        }
    }

    public void MarkFailed(DateTimeOffset nowUtc)
    {
        lock (_gate) _lastAttemptUtc = nowUtc;
    }

    public string BuildTelegramDigest(AiAccessDigestLease lease)
    {
        var events = lease.Events;
        var first = events.Min(x => x.AtUtc);
        var last = events.Max(x => x.AtUtc);
        var visitors = events.GroupBy(x => x.VisitorKey, StringComparer.Ordinal).ToArray();
        var errors = events.Count(x => x.StatusCode >= 400);
        var aiAccounts = events.Where(x => x.IsAuthenticated && string.Equals(x.AccountType, "ai", StringComparison.OrdinalIgnoreCase))
            .Select(x => x.UserId).Where(x => x.HasValue).Distinct().Count();

        var lines = new List<string>
        {
            "🤖 Использование AI-доступа TaskForge",
            string.Empty,
            $"Период: {LocalTime(first):dd.MM HH:mm:ss} — {LocalTime(last):HH:mm:ss}",
            $"Событий: {events.Count} • посетителей: {visitors.Length} • ошибок: {errors}",
            aiAccounts > 0 ? $"AI-аккаунтов: {aiAccounts}" : string.Empty
        };
        if (lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);

        var maxVisitors = Math.Clamp(cfg.GetValue("AiAccessTelemetry:MaxVisitorsPerDigest", 6), 1, 12);
        var ordered = visitors
            .OrderByDescending(g => g.Any(x => x.StatusCode >= 400))
            .ThenByDescending(g => g.Count())
            .Take(maxVisitors)
            .ToArray();

        var index = 1;
        foreach (var group in ordered)
        {
            var items = group.OrderBy(x => x.AtUtc).ToArray();
            var sample = items[^1];
            var agent = AgentLabel(items.Select(x => x.UserAgent).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)));
            var actor = sample.IsAuthenticated
                ? string.Equals(sample.AccountType, "ai", StringComparison.OrdinalIgnoreCase) ? "AI-аккаунт" : "аккаунт пользователя"
                : "анонимно";
            var visitorId = ShortVisitorId(group.Key);
            var ip = DisplayIp(items.Select(x => x.ClientIp).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)));
            var categories = string.Join(", ", items.GroupBy(x => x.Category)
                .OrderByDescending(g => g.Count())
                .Take(5)
                .Select(g => $"{g.Key}×{g.Count()}"));
            var statuses = string.Join(", ", items.GroupBy(x => x.StatusCode)
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key)
                .Take(5)
                .Select(g => $"{g.Key}×{g.Count()}"));
            var targets = items
                .Where(x => !string.IsNullOrWhiteSpace(x.TargetPath))
                .GroupBy(x => $"{x.TargetSite ?? "main"}:{x.TargetPath}")
                .OrderByDescending(g => g.Count())
                .Take(3)
                .Select(g => $"{g.Key}×{g.Count()}")
                .ToArray();
            var ua = Clean(items.Select(x => x.UserAgent).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)), 120);

            lines.Add(string.Empty);
            lines.Add($"{index}) {agent} • {actor} • {items.Length} запросов");
            lines.Add($"   ID: {visitorId} • IP: {ip}");
            var accountName = Clean(items.Select(x => x.AccountName).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)), 64);
            if (!string.IsNullOrWhiteSpace(accountName)) lines.Add($"   Аккаунт: @{accountName}");
            if (sample.UserId.HasValue) lines.Add($"   User: {sample.UserId.Value.ToString("N")[..8]}… • type={sample.AccountType}");
            lines.Add($"   Типы: {categories}");
            if (targets.Length > 0) lines.Add($"   Страницы: {string.Join("; ", targets)}");
            lines.Add($"   HTTP: {statuses} • max {items.Max(x => x.DurationMs)} ms");
            var referrer = Clean(items.Select(x => x.ReferrerHost).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)), 100);
            if (!string.IsNullOrWhiteSpace(referrer)) lines.Add($"   Referrer: {referrer}");
            var lastError = items.LastOrDefault(x => x.StatusCode >= 400);
            if (lastError != null)
            {
                var trace = string.IsNullOrWhiteSpace(lastError.TraceId) ? "—" : ShortVisitorId(lastError.TraceId);
                lines.Add($"   Последняя ошибка: {lastError.StatusCode} {lastError.Operation} • trace {trace}");
            }
            if (!string.IsNullOrWhiteSpace(ua)) lines.Add($"   UA: {ua}");
            index++;
        }

        if (visitors.Length > ordered.Length)
        {
            lines.Add(string.Empty);
            lines.Add($"…ещё посетителей: {visitors.Length - ordered.Length}");
        }

        var pendingAfterLease = Math.Max(0, PendingCount - lease.Count);
        if (pendingAfterLease > 0)
        {
            lines.Add($"Ещё {pendingAfterLease} событий останутся на следующий дайджест.");
        }

        var text = string.Join("\n", lines.Where((line, i) => i == 0 || line.Length > 0 || lines[i - 1].Length > 0));
        return text.Length <= 3900 ? text : text[..3880] + "\n…дайджест сокращён";
    }

    private void CleanupExpired(DateTimeOffset nowUtc)
    {
        var retention = TimeSpan.FromSeconds(Math.Clamp(cfg.GetValue("AiAccessTelemetry:RetentionSeconds", 900), 120, 86400));
        var cutoff = nowUtc - retention;
        _events.RemoveAll(x => x.AtUtc < cutoff);
    }

    private bool IsMeaningfulUse(AiAccessEventDto evt)
    {
        if (evt.StatusCode >= 400) return true;
        if (!string.Equals(evt.Category, "discovery", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(evt.Operation, "ai-access", StringComparison.OrdinalIgnoreCase)) return true;
        return IsKnownAiAgent(evt.UserAgent);
    }

    private static bool IsKnownAiAgent(string? ua)
    {
        var value = ua ?? string.Empty;
        return value.Contains("ChatGPT", StringComparison.OrdinalIgnoreCase)
               || value.Contains("GPTBot", StringComparison.OrdinalIgnoreCase)
               || value.Contains("OpenAI", StringComparison.OrdinalIgnoreCase)
               || value.Contains("Claude", StringComparison.OrdinalIgnoreCase)
               || value.Contains("Anthropic", StringComparison.OrdinalIgnoreCase)
               || value.Contains("Perplexity", StringComparison.OrdinalIgnoreCase)
               || value.Contains("Google-Extended", StringComparison.OrdinalIgnoreCase)
               || value.Contains("Gemini", StringComparison.OrdinalIgnoreCase)
               || value.Contains("Copilot", StringComparison.OrdinalIgnoreCase);
    }

    private string DisplayIp(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "—";
        if (cfg.GetValue("AiAccessTelemetry:IncludeFullIp", false)) return Clean(raw, 64) ?? "—";
        if (!IPAddress.TryParse(raw, out var address)) return "скрыт";
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return $"{bytes[0]}.{bytes[1]}.{bytes[2]}.x";
        }
        var parts = address.ToString().Split(':', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 0 ? "IPv6" : string.Join(":", parts.Take(Math.Min(3, parts.Length))) + ":…";
    }

    private static string AgentLabel(string? ua)
    {
        var value = ua ?? string.Empty;
        if (value.Contains("ChatGPT", StringComparison.OrdinalIgnoreCase) || value.Contains("GPTBot", StringComparison.OrdinalIgnoreCase) || value.Contains("OpenAI", StringComparison.OrdinalIgnoreCase)) return "OpenAI / ChatGPT";
        if (value.Contains("Claude", StringComparison.OrdinalIgnoreCase) || value.Contains("Anthropic", StringComparison.OrdinalIgnoreCase)) return "Claude / Anthropic";
        if (value.Contains("Perplexity", StringComparison.OrdinalIgnoreCase)) return "Perplexity";
        if (value.Contains("Gemini", StringComparison.OrdinalIgnoreCase) || value.Contains("Google-Extended", StringComparison.OrdinalIgnoreCase)) return "Google / Gemini";
        if (value.Contains("Copilot", StringComparison.OrdinalIgnoreCase) || value.Contains("bingbot", StringComparison.OrdinalIgnoreCase)) return "Microsoft / Copilot";
        if (value.Contains("curl", StringComparison.OrdinalIgnoreCase)) return "curl / CLI";
        if (value.Contains("python", StringComparison.OrdinalIgnoreCase)) return "Python client";
        if (value.Contains("Mozilla", StringComparison.OrdinalIgnoreCase)) return "Browser / пользователь";
        return "Неизвестный клиент";
    }

    private static DateTimeOffset LocalTime(DateTimeOffset value)
    {
        try
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Jerusalem");
            return TimeZoneInfo.ConvertTime(value, zone);
        }
        catch
        {
            return value.ToLocalTime();
        }
    }

    private static string ShortVisitorId(string value)
    {
        var clean = Clean(value, 80) ?? "unknown";
        var pos = clean.IndexOf(':');
        var tail = pos >= 0 ? clean[(pos + 1)..] : clean;
        return tail.Length <= 12 ? tail : tail[..12] + "…";
    }

    private static bool IsSane(AiAccessEventDto evt)
        => evt.AtUtc > DateTimeOffset.UtcNow.AddDays(-1)
           && evt.AtUtc < DateTimeOffset.UtcNow.AddMinutes(5)
           && !string.IsNullOrWhiteSpace(evt.Source)
           && !string.IsNullOrWhiteSpace(evt.Category)
           && !string.IsNullOrWhiteSpace(evt.Operation)
           && !string.IsNullOrWhiteSpace(evt.VisitorKey);

    private static AiAccessEventDto Sanitize(AiAccessEventDto evt) => evt with
    {
        Source = Clean(evt.Source, 32) ?? "unknown",
        Category = Clean(evt.Category, 32) ?? "unknown",
        Operation = Clean(evt.Operation, 64) ?? "unknown",
        Method = Clean(evt.Method, 12) ?? "GET",
        VisitorKey = Clean(evt.VisitorKey, 96) ?? "unknown",
        ClientIp = Clean(evt.ClientIp, 64),
        AccountType = Clean(evt.AccountType, 16) ?? "anonymous",
        AccountName = Clean(evt.AccountName, 64),
        UserAgent = Clean(evt.UserAgent, 256),
        TargetSite = Clean(evt.TargetSite, 32),
        TargetPath = Clean(evt.TargetPath, 256),
        ReferrerHost = Clean(evt.ReferrerHost, 128),
        TraceId = Clean(evt.TraceId, 96),
        StatusCode = Math.Clamp(evt.StatusCode, 0, 999),
        DurationMs = Math.Clamp(evt.DurationMs, 0, 600_000)
    };

    private static string? Clean(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = new string(value.Trim().Where(ch => !char.IsControl(ch) || ch == ' ').ToArray());
        return normalized.Length <= max ? normalized : normalized[..max];
    }
}
