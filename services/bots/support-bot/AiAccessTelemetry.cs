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
            foreach (var evt in accepted) AddOrMerge(evt);

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

            if (_lastAttemptUtc != DateTimeOffset.MinValue && nowUtc - _lastAttemptUtc < TimeSpan.FromSeconds(20)) return null;

            var first = _events.Min(x => x.AtUtc);
            var last = _events.Max(x => x.AtUtc);
            var age = nowUtc - first;
            var quiet = nowUtc - last;
            var hasProblem = _events.Any(IsProblem);
            var hasMeaningfulUse = _events.Any(IsMeaningfulUse);
            var discoveryMin = Math.Clamp(cfg.GetValue("AiAccessTelemetry:DiscoveryOnlyMinEvents", 4), 2, 50);
            var quietSeconds = Math.Clamp(cfg.GetValue("AiAccessTelemetry:QuietSeconds", 45), 10, 600);
            var maxDigestSeconds = Math.Clamp(cfg.GetValue("AiAccessTelemetry:MaxDigestIntervalSeconds", 300), 60, 3600);

            var due = hasProblem
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
        var networkSources = events.Select(x => x.VisitorKey).Distinct(StringComparer.Ordinal).ToArray();
        var clientGroups = events.GroupBy(ClientGroupKey, StringComparer.Ordinal).ToArray();
        var serverErrors = events.Count(IsServerError);
        var requestErrors = events.Count(IsActionableClientError);
        var expiredArtifacts = events.Count(IsExpectedArtifactMiss);
        var throttled = events.Count(x => x.StatusCode == 429);
        var canceled = events.Count(x => x.StatusCode == 499);
        var gatewayEvents = events.Count(x => string.Equals(x.Source, "gateway", StringComparison.OrdinalIgnoreCase));
        var aiAccounts = events.Where(x => x.IsAuthenticated && string.Equals(x.AccountType, "ai", StringComparison.OrdinalIgnoreCase))
            .Select(x => x.UserId).Where(x => x.HasValue).Distinct().Count();

        var lines = new List<string>
        {
            "🤖 Использование AI-доступа TaskForge",
            string.Empty,
            $"Период: {LocalTime(first):dd.MM HH:mm:ss} — {LocalTime(last):HH:mm:ss}",
            $"Событий: {events.Count} • сетевых источников: {networkSources.Length} • групп клиентов: {clientGroups.Length}",
            $"Ошибок сервера: {serverErrors} • ошибок запроса: {requestErrors}"
        };

        var protective = new List<string>();
        if (expiredArtifacts > 0) protective.Add($"истёкшие artifact×{expiredArtifacts}");
        if (throttled > 0) protective.Add($"лимит 429×{throttled}");
        if (canceled > 0) protective.Add($"отмена клиентом 499×{canceled}");
        if (protective.Count > 0) lines.Add($"Служебные/защитные события: {string.Join(", ", protective)}");
        if (gatewayEvents > 0) lines.Add($"Gateway перехватил до Browser API: {gatewayEvents} событий");
        if (aiAccounts > 0) lines.Add($"AI-аккаунтов: {aiAccounts}");

        var maxGroups = Math.Clamp(
            cfg.GetValue("AiAccessTelemetry:MaxClientGroupsPerDigest", cfg.GetValue("AiAccessTelemetry:MaxVisitorsPerDigest", 6)),
            1,
            12);
        var ordered = clientGroups
            .OrderByDescending(g => g.Any(IsProblem))
            .ThenByDescending(g => g.Count())
            .Take(maxGroups)
            .ToArray();

        var index = 1;
        foreach (var group in ordered)
        {
            var items = group.OrderBy(x => x.AtUtc).ToArray();
            var sample = items[^1];
            var agent = AgentLabel(items);
            var actor = sample.IsAuthenticated
                ? string.Equals(sample.AccountType, "ai", StringComparison.OrdinalIgnoreCase) ? "AI-аккаунт" : "аккаунт пользователя"
                : "анонимно";
            var groupNetworks = items.Select(x => x.VisitorKey).Distinct(StringComparer.Ordinal).ToArray();
            var visitorId = ShortVisitorId(groupNetworks[0]);
            var distinctIps = items.Select(x => DisplayIp(x.ClientIp)).Where(x => x != "—").Distinct(StringComparer.Ordinal).Take(3).ToArray();
            var ip = distinctIps.Length == 0 ? "—" : string.Join(", ", distinctIps);
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
            lines.Add($"{index}. {agent} • {actor} • {items.Length} запросов");
            lines.Add($"   ID сети: {visitorId}{(groupNetworks.Length > 1 ? $" (+{groupNetworks.Length - 1})" : string.Empty)} • IP: {ip}");
            var accountName = Clean(items.Select(x => x.AccountName).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)), 64);
            if (!string.IsNullOrWhiteSpace(accountName)) lines.Add($"   Аккаунт: @{accountName}");
            if (sample.UserId.HasValue) lines.Add($"   User: {sample.UserId.Value.ToString("N")[..8]}… • type={sample.AccountType}");
            lines.Add($"   Типы: {categories}");
            if (targets.Length > 0) lines.Add($"   Страницы: {string.Join("; ", targets)}");
            lines.Add($"   HTTP: {statuses} • max {items.Max(x => x.DurationMs)} ms");

            var sourceBreakdown = items.GroupBy(x => x.Source, StringComparer.OrdinalIgnoreCase).ToArray();
            if (sourceBreakdown.Length > 1 || sourceBreakdown.Any(x => string.Equals(x.Key, "gateway", StringComparison.OrdinalIgnoreCase)))
            {
                lines.Add($"   Telemetry: {string.Join(", ", sourceBreakdown.OrderByDescending(x => x.Count()).Select(x => $"{x.Key}×{x.Count()}"))}");
            }

            var service = ServiceSummary(items);
            if (!string.IsNullOrWhiteSpace(service)) lines.Add($"   Служебные: {service}");

            var referrer = Clean(items.Select(x => x.ReferrerHost).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)), 100);
            if (!string.IsNullOrWhiteSpace(referrer)) lines.Add($"   Referrer: {referrer}");
            var lastProblem = items.LastOrDefault(IsProblem);
            if (lastProblem != null)
            {
                var trace = string.IsNullOrWhiteSpace(lastProblem.TraceId) ? "—" : ShortVisitorId(lastProblem.TraceId);
                lines.Add($"   Последняя ошибка: {lastProblem.StatusCode} {lastProblem.Operation} • trace {trace}");
            }
            if (!string.IsNullOrWhiteSpace(ua)) lines.Add($"   UA: {ua}");
            index++;
        }

        if (clientGroups.Length > ordered.Length)
        {
            lines.Add(string.Empty);
            lines.Add($"…ещё групп клиентов: {clientGroups.Length - ordered.Length}");
        }

        var pendingAfterLease = Math.Max(0, PendingCount - lease.Count);
        if (pendingAfterLease > 0)
        {
            lines.Add($"Ещё {pendingAfterLease} событий останутся на следующий дайджест.");
        }

        var text = string.Join("\n", lines.Where((line, i) => i == 0 || line.Length > 0 || lines[i - 1].Length > 0));
        return text.Length <= 3900 ? text : text[..3880] + "\n…дайджест сокращён";
    }

    private void AddOrMerge(AiAccessEventDto evt)
    {
        if (!string.IsNullOrWhiteSpace(evt.TraceId))
        {
            var index = _events.FindIndex(existing =>
                string.Equals(existing.TraceId, evt.TraceId, StringComparison.Ordinal)
                && existing.StatusCode == evt.StatusCode
                && string.Equals(existing.Method, evt.Method, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                var existing = _events[index];
                if (string.Equals(existing.Source, "gateway", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(evt.Source, "browser-api", StringComparison.OrdinalIgnoreCase))
                {
                    _events[index] = evt;
                }
                return;
            }
        }

        _events.Add(evt);
    }

    private void CleanupExpired(DateTimeOffset nowUtc)
    {
        var retention = TimeSpan.FromSeconds(Math.Clamp(cfg.GetValue("AiAccessTelemetry:RetentionSeconds", 900), 120, 86400));
        var cutoff = nowUtc - retention;
        _events.RemoveAll(x => x.AtUtc < cutoff);
    }

    private static bool IsProblem(AiAccessEventDto evt)
        => IsServerError(evt) || IsActionableClientError(evt);

    private static bool IsServerError(AiAccessEventDto evt)
        => evt.StatusCode >= 500;

    private static bool IsActionableClientError(AiAccessEventDto evt)
        => evt.StatusCode is >= 400 and < 500
           && evt.StatusCode is not 429 and not 499
           && !IsExpectedArtifactMiss(evt);

    private static bool IsExpectedArtifactMiss(AiAccessEventDto evt)
        => evt.StatusCode == 410
           && string.Equals(evt.Category, "artifact", StringComparison.OrdinalIgnoreCase);

    private bool IsMeaningfulUse(AiAccessEventDto evt)
    {
        if (evt.StatusCode >= 400) return true;
        if (!string.Equals(evt.Category, "discovery", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(evt.Operation, "ai-access", StringComparison.OrdinalIgnoreCase)) return true;
        return IsKnownAiAgent(evt.UserAgent);
    }

    private static string ServiceSummary(IReadOnlyCollection<AiAccessEventDto> items)
    {
        var parts = new List<string>();
        var expired = items.Count(IsExpectedArtifactMiss);
        var throttled = items.Count(x => x.StatusCode == 429);
        var canceled = items.Count(x => x.StatusCode == 499);
        if (expired > 0) parts.Add($"artifact expired×{expired}");
        if (throttled > 0) parts.Add($"rate-limit×{throttled}");
        if (canceled > 0) parts.Add($"client-cancel×{canceled}");
        return string.Join(", ", parts);
    }

    private static string ClientGroupKey(AiAccessEventDto evt)
    {
        if (evt.IsAuthenticated && evt.UserId.HasValue) return $"user:{evt.UserId.Value:N}";
        var family = AgentFamily(evt.UserAgent);
        return family is null ? $"network:{evt.VisitorKey}" : $"agent:{family}";
    }

    private static string? AgentFamily(string? ua)
    {
        var value = ua ?? string.Empty;
        if (value.Contains("ChatGPT", StringComparison.OrdinalIgnoreCase) || value.Contains("GPTBot", StringComparison.OrdinalIgnoreCase) || value.Contains("OpenAI", StringComparison.OrdinalIgnoreCase)) return "openai";
        if (value.Contains("Claude", StringComparison.OrdinalIgnoreCase) || value.Contains("Anthropic", StringComparison.OrdinalIgnoreCase)) return "anthropic";
        if (value.Contains("Perplexity", StringComparison.OrdinalIgnoreCase)) return "perplexity";
        if (value.Contains("Gemini", StringComparison.OrdinalIgnoreCase) || value.Contains("Google-Extended", StringComparison.OrdinalIgnoreCase)) return "google";
        if (value.Contains("Copilot", StringComparison.OrdinalIgnoreCase) || value.Contains("bingbot", StringComparison.OrdinalIgnoreCase)) return "microsoft";
        return null;
    }

    private static bool IsKnownAiAgent(string? ua) => AgentFamily(ua) is not null;

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

    private static string AgentLabel(IReadOnlyCollection<AiAccessEventDto> items)
    {
        var known = items.Select(x => AgentFamily(x.UserAgent)).FirstOrDefault(x => x is not null);
        if (known is not null)
        {
            return known switch
            {
                "openai" => "OpenAI / ChatGPT",
                "anthropic" => "Claude / Anthropic",
                "perplexity" => "Perplexity",
                "google" => "Google / Gemini",
                "microsoft" => "Microsoft / Copilot",
                _ => "AI client"
            };
        }

        var agents = items.Select(x => x.UserAgent ?? string.Empty).Where(x => x.Length > 0).ToArray();
        if (agents.Any(x => x.Contains("HeadlessChrome", StringComparison.OrdinalIgnoreCase) || x.Contains("Playwright", StringComparison.OrdinalIgnoreCase)))
            return "Browser automation";
        if (agents.Any(x => x.Contains("curl", StringComparison.OrdinalIgnoreCase))) return "curl / CLI";
        if (agents.Any(x => x.Contains("python", StringComparison.OrdinalIgnoreCase))) return "Python client";
        if (agents.Any(x => x.Contains("Mozilla", StringComparison.OrdinalIgnoreCase)))
        {
            var families = agents.Select(BrowserFamily).Where(x => x is not null).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            return families > 1 ? "Browser automation / browser-like" : "Browser-like client";
        }
        return "Неизвестный клиент";
    }

    private static string? BrowserFamily(string ua)
    {
        if (ua.Contains("Edg/", StringComparison.OrdinalIgnoreCase)) return "edge";
        if (ua.Contains("Firefox/", StringComparison.OrdinalIgnoreCase)) return "firefox";
        if (ua.Contains("Chrome/", StringComparison.OrdinalIgnoreCase) || ua.Contains("Chromium/", StringComparison.OrdinalIgnoreCase)) return "chrome";
        if (ua.Contains("Safari/", StringComparison.OrdinalIgnoreCase)) return "safari";
        return null;
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
