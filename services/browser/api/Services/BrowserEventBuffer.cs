using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using TaskForge.Browser.Api.Contracts;

namespace TaskForge.Browser.Api.Services;

public sealed partial class BrowserEventBuffer(int maxEntries)
{
    private readonly int _maxEntries = System.Math.Clamp(maxEntries, 20, 1000);
    private readonly ConcurrentQueue<BrowserConsoleEntry> _console = new();
    private readonly ConcurrentQueue<BrowserNetworkEntry> _networkFailures = new();
    private readonly ConcurrentQueue<BrowserNetworkEntry> _httpErrors = new();

    public void AddConsole(string type, string text)
    {
        _console.Enqueue(new BrowserConsoleEntry(Trim(type, 40), Redact(text, 2000), DateTimeOffset.UtcNow));
        TrimQueue(_console);
    }

    public void AddFailure(string method, string url, string resourceType, string? failure)
    {
        _networkFailures.Enqueue(new BrowserNetworkEntry(Trim(method, 16), SafeUrl(url), Trim(resourceType, 40), null, Redact(failure, 500), DateTimeOffset.UtcNow));
        TrimQueue(_networkFailures);
    }

    public void AddHttpError(string method, string url, string resourceType, int status)
    {
        _httpErrors.Enqueue(new BrowserNetworkEntry(Trim(method, 16), SafeUrl(url), Trim(resourceType, 40), status, null, DateTimeOffset.UtcNow));
        TrimQueue(_httpErrors);
    }

    public List<BrowserConsoleEntry> Console() => _console.ToList();
    public List<BrowserNetworkEntry> NetworkFailures() => _networkFailures.ToList();
    public List<BrowserNetworkEntry> HttpErrors() => _httpErrors.ToList();

    private void TrimQueue<T>(ConcurrentQueue<T> queue)
    {
        while (queue.Count > _maxEntries) queue.TryDequeue(out _);
    }

    private static string SafeUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return Redact(value, 1000);
        return uri.GetLeftPart(UriPartial.Path);
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
}
