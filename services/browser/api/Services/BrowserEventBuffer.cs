using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using TaskForge.Browser.Api.Contracts;

namespace TaskForge.Browser.Api.Services;

public sealed partial class BrowserEventBuffer(int maxEntries)
{
    private readonly int _maxEntries = System.Math.Clamp(maxEntries, 20, 1000);
    private readonly ConcurrentQueue<BrowserConsoleEntry> _console = new();
    private readonly ConcurrentQueue<BrowserNetworkEntry> _networkFailures = new();
    private readonly ConcurrentQueue<BrowserNetworkEntry> _httpErrors = new();
    private readonly ConcurrentQueue<BrowserPolicyBlockedEntry> _policyBlocked = new();
    private readonly ConcurrentDictionary<string, int> _pending = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _expectedFailures = new(StringComparer.Ordinal);

    public int PendingRequestCount => _pending.Values.Sum();

    public void AddConsole(string type, string text)
    {
        if (IsExpectedInspectorConsole(type, text)) return;

        _console.Enqueue(new BrowserConsoleEntry(Trim(type, 40), Redact(text, 2000), DateTimeOffset.UtcNow));
        TrimQueue(_console);
    }

    public void RequestStarted(string method, string url, string resourceType)
        => _pending.AddOrUpdate(Fingerprint(method, url, resourceType), 1, static (_, count) => count + 1);

    public void RequestFinished(string method, string url, string resourceType)
        => Decrement(_pending, Fingerprint(method, url, resourceType));

    public void RequestFailed(string method, string url, string resourceType, string? failure)
    {
        var fingerprint = Fingerprint(method, url, resourceType);
        Decrement(_pending, fingerprint);
        if (ConsumeExpectedFailure(fingerprint)) return;

        _networkFailures.Enqueue(new BrowserNetworkEntry(
            Trim(method, 16),
            SafeUrl(url),
            Trim(resourceType, 40),
            null,
            Redact(failure, 500),
            DateTimeOffset.UtcNow));
        TrimQueue(_networkFailures);
    }

    public void AddPolicyBlocked(string method, string url, string resourceType, string reason)
    {
        var fingerprint = Fingerprint(method, url, resourceType);
        _expectedFailures.AddOrUpdate(fingerprint, 1, static (_, count) => count + 1);
        _policyBlocked.Enqueue(new BrowserPolicyBlockedEntry(
            Trim(method, 16),
            SafeUrl(url),
            Trim(resourceType, 40),
            Trim(reason, 100),
            true,
            DateTimeOffset.UtcNow));
        TrimQueue(_policyBlocked);
    }

    public void CancelExpectedFailure(string method, string url, string resourceType)
        => Decrement(_expectedFailures, Fingerprint(method, url, resourceType));

    public void AddHttpError(string method, string url, string resourceType, int status)
    {
        _httpErrors.Enqueue(new BrowserNetworkEntry(Trim(method, 16), SafeUrl(url), Trim(resourceType, 40), status, null, DateTimeOffset.UtcNow));
        TrimQueue(_httpErrors);
    }

    public List<BrowserConsoleEntry> Console() => _console.ToList();
    public List<BrowserNetworkEntry> NetworkFailures() => _networkFailures.ToList();
    public List<BrowserNetworkEntry> HttpErrors() => _httpErrors.ToList();
    public List<BrowserPolicyBlockedEntry> PolicyBlockedRequests() => _policyBlocked.ToList();

    public List<string> PendingRequests(int max = 20)
        => _pending
            .Where(pair => pair.Value > 0)
            .Select(pair => new { Key = DiagnosticFingerprint(pair.Key), pair.Value })
            .GroupBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(group => new { Key = group.Key, Value = group.Sum(pair => pair.Value) })
            .OrderByDescending(pair => pair.Value)
            .Take(System.Math.Clamp(max, 1, 100))
            .Select(pair => pair.Value == 1 ? pair.Key : $"{pair.Key} (x{pair.Value})")
            .ToList();

    private bool ConsumeExpectedFailure(string fingerprint)
    {
        while (_expectedFailures.TryGetValue(fingerprint, out var count))
        {
            if (count <= 1)
            {
                if (_expectedFailures.TryRemove(fingerprint, out _)) return true;
            }
            else if (_expectedFailures.TryUpdate(fingerprint, count - 1, count))
            {
                return true;
            }
        }

        return false;
    }

    private static void Decrement(ConcurrentDictionary<string, int> dictionary, string key)
    {
        while (dictionary.TryGetValue(key, out var count))
        {
            if (count <= 1)
            {
                if (dictionary.TryRemove(key, out _)) return;
            }
            else if (dictionary.TryUpdate(key, count - 1, count))
            {
                return;
            }
        }
    }

    private void TrimQueue<T>(ConcurrentQueue<T> queue)
    {
        while (queue.Count > _maxEntries) queue.TryDequeue(out _);
    }

    private static string Fingerprint(string method, string url, string resourceType)
    {
        // Keep query strings out of diagnostics while still distinguishing requests
        // that share a path but carry different parameters. This prevents one
        // expected inspector block from hiding an unrelated genuine failure.
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..12].ToLowerInvariant();
        return $"{Trim(method, 16).ToUpperInvariant()} {Trim(resourceType, 40)} {SafeUrl(url)} [{hash}]";
    }

    private static string DiagnosticFingerprint(string fingerprint)
        => FingerprintHashPattern().Replace(fingerprint, string.Empty);

    private static string SafeUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return Redact(value, 1000);
        return uri.GetLeftPart(UriPartial.Path);
    }

    private bool IsExpectedInspectorConsole(string type, string text)
    {
        if (!string.Equals(type, "error", StringComparison.OrdinalIgnoreCase)) return false;

        if (_policyBlocked.IsEmpty) return false;

        return text.Contains("ERR_BLOCKED_BY_CLIENT", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("blocked by client", StringComparison.OrdinalIgnoreCase);
    }

    private static string Redact(string? value, int max)
    {
        var text = value ?? string.Empty;
        text = TokenPattern().Replace(text, "$1=[redacted]");
        text = BearerPattern().Replace(text, "Bearer [redacted]");
        return Trim(text, max);
    }

    private static string Trim(string? value, int max)
    {
        var text = value ?? string.Empty;
        return text.Length <= max ? text : text[..max] + "…";
    }

    [GeneratedRegex("(?i)(access_token|refresh_token|token|password|secret|code)=([^&\\s]+)")]
    private static partial Regex TokenPattern();

    [GeneratedRegex("(?i)Bearer\\s+[A-Za-z0-9._~-]+")]
    private static partial Regex BearerPattern();

    [GeneratedRegex("\\s\\[[0-9a-f]{12}\\]$")]
    private static partial Regex FingerprintHashPattern();
}
