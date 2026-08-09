using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TaskForge.SupportBot;

public sealed class GatewayAiAccessLogIngestor(
    IConfiguration cfg,
    AiAccessDigestAggregator aggregator,
    ILogger<GatewayAiAccessLogIngestor> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!cfg.GetValue("AiAccessTelemetry:Enabled", true)) return;

        var path = cfg["AiAccessTelemetry:GatewayLogPath"]?.Trim();
        if (string.IsNullOrWhiteSpace(path)) path = "/var/log/taskforge-gateway/edge-events.log";

        while (!stoppingToken.IsCancellationRequested)
        {
            if (!File.Exists(path))
            {
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                continue;
            }

            try
            {
                await TailAsync(path, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Gateway AI telemetry tail failed for {Path}; reopening shortly.", path);
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
        }
    }

    private async Task TailAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 4096,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);

        // Gateway events are delivery telemetry, not an audit log. Start at EOF so
        // a support-bot restart never replays old 429/499 events into Telegram.
        stream.Seek(0, SeekOrigin.End);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
        logger.LogInformation("Gateway AI access telemetry tail attached. path={Path} offset={Offset}", path, stream.Position);

        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is not null)
            {
                var evt = Parse(line);
                if (evt is not null) aggregator.Record([evt]);
                continue;
            }

            if (!File.Exists(path)) return;
            var currentLength = new FileInfo(path).Length;
            if (currentLength < stream.Position) return;
            await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
        }
    }

    private AiAccessEventDto? Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;

        try
        {
            var raw = JsonSerializer.Deserialize<GatewayLogLine>(line, JsonOptions);
            if (raw is null || string.IsNullOrWhiteSpace(raw.Uri) || !int.TryParse(raw.Status, out var status)) return null;
            if (status != 429 && status != 499 && (status < 500 || status > 599)) return null;

            var at = DateTimeOffset.TryParse(raw.At, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var parsedAt)
                ? parsedAt
                : DateTimeOffset.UtcNow;
            var durationMs = double.TryParse(raw.RequestTime, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
                ? (long)Math.Clamp(seconds * 1000d, 0d, 600_000d)
                : 0L;
            var ip = ParseIp(raw.Remote);
            var (category, operation, site, targetPath) = Classify(raw.Uri);

            return new AiAccessEventDto(
                at.ToUniversalTime(),
                "gateway",
                category,
                operation,
                string.IsNullOrWhiteSpace(raw.Method) ? "GET" : raw.Method,
                status,
                durationMs,
                VisitorKey(ip),
                ip,
                false,
                "anonymous",
                null,
                null,
                CleanDash(raw.UserAgent),
                site,
                targetPath,
                ReferrerHost(raw.Referrer),
                CleanDash(raw.RequestId));
        }
        catch (JsonException ex)
        {
            logger.LogDebug(ex, "Ignoring malformed gateway AI telemetry line.");
            return null;
        }
    }

    private static (string Category, string Operation, string? Site, string? TargetPath) Classify(string uri)
    {
        var path = uri.Split('?', 2)[0];
        if (path.StartsWith("/api/site/agent/capture/", StringComparison.OrdinalIgnoreCase))
        {
            var tail = path["/api/site/agent/capture/".Length..];
            var parts = tail.Split('/', 5, StringSplitOptions.RemoveEmptyEntries);
            var site = parts.ElementAtOrDefault(0);
            var mode = parts.ElementAtOrDefault(3) ?? "capture";
            var target = parts.Length >= 5 ? "/" + parts[4] : "/";
            return ("capture", mode, site, target);
        }

        if (path.StartsWith("/ai-artifacts/", StringComparison.OrdinalIgnoreCase))
        {
            var file = path.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "artifact";
            return ("artifact", file, null, null);
        }

        if (path.Equals("/api/site/snapshot", StringComparison.OrdinalIgnoreCase)) return ("snapshot", "snapshot", null, null);
        if (path.Equals("/api/site/render", StringComparison.OrdinalIgnoreCase)) return ("render", "png", null, null);
        if (path.Equals("/api/site/render.pdf", StringComparison.OrdinalIgnoreCase)) return ("render", "pdf", null, null);
        if (path.StartsWith("/api/browser/sessions", StringComparison.OrdinalIgnoreCase)) return ("session", "gateway", null, null);
        if (path.StartsWith("/api/site/", StringComparison.OrdinalIgnoreCase) || path.StartsWith("/api/browser/", StringComparison.OrdinalIgnoreCase)) return ("ai", "gateway", null, null);
        return ("ai", "gateway", null, null);
    }

    private static string VisitorKey(string? ip)
    {
        var network = "unknown";
        if (IPAddress.TryParse(ip, out var address)) network = address.MapToIPv6().ToString();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"network:{network}"))).ToLowerInvariant()[..24];
        return $"anonymous:{hash}";
    }

    private static string? ParseIp(string? value)
        => IPAddress.TryParse(CleanDash(value), out var ip) ? ip.ToString() : null;

    private static string? ReferrerHost(string? value)
        => Uri.TryCreate(CleanDash(value), UriKind.Absolute, out var uri) ? uri.Host : null;

    private static string? CleanDash(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value == "-") return null;
        return value.Trim();
    }

    private sealed record GatewayLogLine(
        string? At,
        string? RequestId,
        string? Remote,
        string? Host,
        string? Method,
        string? Uri,
        string? Status,
        string? RequestTime,
        string? UpstreamStatus,
        string? UserAgent,
        string? Referrer);
}
