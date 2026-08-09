using System.Net;
using System.Net.Http.Json;
using System.Threading.Channels;
using TaskForge.Browser.Api.Security;

namespace TaskForge.Browser.Api.Services;

public sealed record AiAccessTelemetryEvent(
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

public sealed record AiAccessTelemetryBatch(IReadOnlyList<AiAccessTelemetryEvent> Events);

public sealed class AiAccessTelemetryReporter(
    IConfiguration cfg,
    IHttpClientFactory httpClientFactory,
    BrowserCallerResolver callerResolver,
    ILogger<AiAccessTelemetryReporter> logger) : BackgroundService
{
    private readonly Channel<AiAccessTelemetryEvent> _channel = Channel.CreateBounded<AiAccessTelemetryEvent>(
        new BoundedChannelOptions(2048)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false
        });

    private long _dropped;

    public bool Enabled => cfg.GetValue("AiAccessTelemetry:Enabled", true);

    public static bool IsTrackedPath(PathString path)
    {
        var value = path.Value ?? string.Empty;
        return value.Equals("/.well-known/taskforge-ai.json", StringComparison.OrdinalIgnoreCase)
               || value.Equals("/.well-known/taskforge-ai-browser.json", StringComparison.OrdinalIgnoreCase)
               || value.Equals("/ai-browser", StringComparison.OrdinalIgnoreCase)
               || value.StartsWith("/api/ai/browser", StringComparison.OrdinalIgnoreCase)
               || value.Equals("/llms.txt", StringComparison.OrdinalIgnoreCase)
               || value.Equals("/ai-access", StringComparison.OrdinalIgnoreCase)
               || value.Equals("/api/browser/openapi.json", StringComparison.OrdinalIgnoreCase)
               || value.Equals("/api/site/info", StringComparison.OrdinalIgnoreCase)
               || value.Equals("/api/site/routes", StringComparison.OrdinalIgnoreCase)
               || value.Equals("/api/site/snapshot", StringComparison.OrdinalIgnoreCase)
               || value.Equals("/api/site/render", StringComparison.OrdinalIgnoreCase)
               || value.Equals("/api/site/render.pdf", StringComparison.OrdinalIgnoreCase)
               || value.StartsWith("/api/site/agent/capture/", StringComparison.OrdinalIgnoreCase)
               || value.StartsWith("/ai-artifacts/", StringComparison.OrdinalIgnoreCase)
               || value.Equals("/api/browser/sessions", StringComparison.OrdinalIgnoreCase)
               || value.StartsWith("/api/browser/sessions/", StringComparison.OrdinalIgnoreCase);
    }

    public void Capture(HttpContext http, long elapsedMs)
    {
        if (!Enabled || !IsTrackedPath(http.Request.Path)) return;

        try
        {
            var caller = callerResolver.Resolve(http);
            var (category, operation, targetSite, targetPath) = Classify(http);
            var clientIp = ResolveClientIp(http);
            var refererHost = ResolveReferrerHost(http.Request.Headers.Referer.ToString());
            var evt = new AiAccessTelemetryEvent(
                DateTimeOffset.UtcNow,
                "browser-api",
                category,
                operation,
                http.Request.Method,
                http.Response.StatusCode,
                Math.Clamp(elapsedMs, 0, 600_000),
                caller.OwnerKey,
                clientIp,
                caller.IsAuthenticated,
                caller.IsAuthenticated ? caller.AccountType : "anonymous",
                caller.UserId,
                null,
                Clamp(http.Request.Headers.UserAgent.ToString(), 256),
                targetSite,
                targetPath,
                refererHost,
                Clamp(http.Request.Headers["X-TaskForge-Gateway-Request-Id"].FirstOrDefault() ?? http.TraceIdentifier, 96));

            if (!_channel.Writer.TryWrite(evt))
            {
                var dropped = Interlocked.Increment(ref _dropped);
                if (dropped == 1 || dropped % 100 == 0)
                {
                    logger.LogWarning("AI access telemetry channel is full; dropped events={Dropped}.", dropped);
                }
            }
        }
        catch (Exception ex)
        {
            // Telemetry is intentionally fail-open. It must never break Browser API traffic.
            logger.LogDebug(ex, "Failed to enqueue AI access telemetry event.");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!Enabled)
        {
            logger.LogInformation("AI access telemetry reporter is disabled.");
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                var batch = Drain(100);
                if (batch.Count == 0) continue;
                await SendBatchAsync(batch, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }

        var remaining = Drain(100);
        if (remaining.Count > 0)
        {
            try { await SendBatchAsync(remaining, CancellationToken.None); }
            catch { /* shutdown is best-effort */ }
        }
    }

    private List<AiAccessTelemetryEvent> Drain(int max)
    {
        var batch = new List<AiAccessTelemetryEvent>(max);
        while (batch.Count < max && _channel.Reader.TryRead(out var evt)) batch.Add(evt);
        return batch;
    }

    private async Task SendBatchAsync(IReadOnlyList<AiAccessTelemetryEvent> batch, CancellationToken ct)
    {
        var key = FirstNonEmpty(
            cfg["InternalApi:Key"],
            cfg["TaskForgeInternalApi:ApiKey"],
            cfg["TaskForge:InternalKey"],
            Environment.GetEnvironmentVariable("TASKFORGE_INTERNAL_KEY"));
        if (string.IsNullOrWhiteSpace(key))
        {
            logger.LogWarning("AI access telemetry cannot be delivered because InternalApi:Key is not configured.");
            return;
        }

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, "api/internal/ai-access/events")
                {
                    Content = JsonContent.Create(new AiAccessTelemetryBatch(batch))
                };
                request.Headers.TryAddWithoutValidation("X-Internal-Key", key);
                using var response = await httpClientFactory.CreateClient("support-bot").SendAsync(request, ct);
                if (response.IsSuccessStatusCode) return;

                logger.LogWarning("Support-bot rejected AI access telemetry batch. status={Status} count={Count}", (int)response.StatusCode, batch.Count);
                if ((int)response.StatusCode < 500) return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to deliver AI access telemetry batch to support-bot. attempt={Attempt} count={Count}", attempt, batch.Count);
            }

            if (attempt < 2) await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
        }
    }

    private static (string Category, string Operation, string? Site, string? TargetPath) Classify(HttpContext http)
    {
        var path = http.Request.Path.Value ?? string.Empty;
        if (path.Equals("/.well-known/taskforge-ai.json", StringComparison.OrdinalIgnoreCase)) return ("discovery", "discovery-json", null, null);
        if (path.Equals("/.well-known/taskforge-ai-browser.json", StringComparison.OrdinalIgnoreCase)) return ("discovery", "remote-browser-discovery", null, null);
        if (path.Equals("/ai-browser", StringComparison.OrdinalIgnoreCase)) return ("discovery", "remote-browser-workbench", null, null);
        if (path.Equals("/llms.txt", StringComparison.OrdinalIgnoreCase)) return ("discovery", "llms", null, null);
        if (path.Equals("/ai-access", StringComparison.OrdinalIgnoreCase)) return ("discovery", "ai-access", null, null);
        if (path.Equals("/api/browser/openapi.json", StringComparison.OrdinalIgnoreCase)) return ("discovery", "openapi", null, null);
        if (path.Equals("/api/site/info", StringComparison.OrdinalIgnoreCase)) return ("discovery", "site-info", null, null);
        if (path.Equals("/api/site/routes", StringComparison.OrdinalIgnoreCase)) return ("discovery", "route-catalog", null, null);
        if (path.Equals("/api/site/snapshot", StringComparison.OrdinalIgnoreCase)) return ("snapshot", "snapshot", SafeQuery(http, "site"), SafeTargetPath(SafeQuery(http, "path")));
        if (path.Equals("/api/site/render", StringComparison.OrdinalIgnoreCase)) return ("render", "png", SafeQuery(http, "site"), SafeTargetPath(SafeQuery(http, "path")));
        if (path.Equals("/api/site/render.pdf", StringComparison.OrdinalIgnoreCase)) return ("render", "pdf", SafeQuery(http, "site"), SafeTargetPath(SafeQuery(http, "path")));

        if (path.StartsWith("/api/site/agent/capture/", StringComparison.OrdinalIgnoreCase))
        {
            var tail = path["/api/site/agent/capture/".Length..];
            var parts = tail.Split('/', 5, StringSplitOptions.RemoveEmptyEntries);
            var site = parts.ElementAtOrDefault(0);
            var mode = parts.ElementAtOrDefault(3) ?? "capture";
            var target = parts.Length >= 5 ? "/" + parts[4] : "/";
            return ("capture", mode, Clamp(site, 32), SafeTargetPath(target));
        }

        if (path.StartsWith("/ai-artifacts/", StringComparison.OrdinalIgnoreCase))
        {
            var file = path.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "artifact";
            return ("artifact", Clamp(file, 48) ?? "artifact", null, null);
        }

        if (path.Equals("/api/browser/sessions", StringComparison.OrdinalIgnoreCase))
        {
            return ("session", http.Request.Method.Equals("POST", StringComparison.OrdinalIgnoreCase) ? "create" : "sessions", null, null);
        }

        if (path.StartsWith("/api/ai/browser", StringComparison.OrdinalIgnoreCase))
        {
            // Never inspect query values here: capability keys and fill values live there for GET-only clients.
            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var operation = "remote-browser";
            if (path.Equals("/api/ai/browser/start", StringComparison.OrdinalIgnoreCase)) operation = "remote-start";
            else if (path.Equals("/api/ai/browser/confirm", StringComparison.OrdinalIgnoreCase)) operation = "remote-confirm";
            else if (segments.Length >= 6) operation = "remote-" + (segments.ElementAtOrDefault(5) ?? "session");
            return (operation.Contains("screenshot", StringComparison.OrdinalIgnoreCase) || operation.Contains("view", StringComparison.OrdinalIgnoreCase) ? "render" : "session", Clamp(operation, 48) ?? "remote-browser", null, null);
        }

        if (path.StartsWith("/api/browser/sessions/", StringComparison.OrdinalIgnoreCase))
        {
            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var action = segments.Length >= 5
                ? segments[4]
                : http.Request.Method.Equals("DELETE", StringComparison.OrdinalIgnoreCase) ? "close" : "session";
            return ("session", Clamp(action, 48) ?? "session", null, null);
        }

        return ("ai", "unknown", null, null);
    }

    private static string? SafeQuery(HttpContext http, string name)
    {
        var value = http.Request.Query[name].FirstOrDefault();
        return Clamp(value, 256);
    }

    private static string? SafeTargetPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var cut = value.IndexOfAny(['?', '#']);
        if (cut >= 0) value = value[..cut];
        return Clamp(value, 256);
    }

    private static string? ResolveClientIp(HttpContext http)
    {
        foreach (var value in new[]
                 {
                     http.Request.Headers["X-TaskForge-Client-IP"].FirstOrDefault(),
                     http.Request.Headers["X-Real-IP"].FirstOrDefault(),
                     http.Connection.RemoteIpAddress?.ToString()
                 })
        {
            if (IPAddress.TryParse(value, out var address)) return address.ToString();
        }
        return null;
    }

    private static string? ResolveReferrerHost(string? raw)
        => Uri.TryCreate(raw, UriKind.Absolute, out var uri) ? Clamp(uri.Host, 128) : null;

    private static string? Clamp(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = new string(value.Trim().Where(ch => !char.IsControl(ch) || ch == ' ').ToArray());
        return normalized.Length <= max ? normalized : normalized[..max];
    }

    private static string? FirstNonEmpty(params string?[] values)
        => values.Select(value => (value ?? string.Empty).Trim()).FirstOrDefault(value => value.Length > 0);
}
