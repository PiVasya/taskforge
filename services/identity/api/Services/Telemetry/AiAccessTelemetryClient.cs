using System.Net.Http.Json;
using System.Threading.Channels;

namespace TaskForge.Identity.Api.Services.Telemetry;

public sealed record AiIdentityAccessEvent(
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

public sealed record AiIdentityAccessBatch(IReadOnlyList<AiIdentityAccessEvent> Events);

public sealed class AiAccessTelemetryClient(
    IConfiguration cfg,
    IHttpClientFactory clients,
    ILogger<AiAccessTelemetryClient> logger) : BackgroundService
{
    private readonly Channel<AiIdentityAccessEvent> _channel = Channel.CreateBounded<AiIdentityAccessEvent>(
        new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false
        });

    public void RecordAiAccountEvent(HttpContext http, Guid userId, string? accountName, string operation)
    {
        if (!cfg.GetValue("AiAccessTelemetry:Enabled", true)) return;
        var evt = new AiIdentityAccessEvent(
            DateTimeOffset.UtcNow,
            "identity-api",
            "identity",
            operation,
            http.Request.Method,
            StatusCodes.Status200OK,
            0,
            $"user:{userId:N}",
            http.Connection.RemoteIpAddress?.ToString(),
            true,
            "ai",
            userId,
            Clamp(accountName, 64),
            Clamp(http.Request.Headers.UserAgent.ToString(), 256),
            "main",
            http.Request.Path.Value,
            ReferrerHost(http.Request.Headers.Referer.ToString()),
            Clamp(http.TraceIdentifier, 96));
        _channel.Writer.TryWrite(evt);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!cfg.GetValue("AiAccessTelemetry:Enabled", true)) return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                var batch = new List<AiIdentityAccessEvent>(50);
                while (batch.Count < 50 && _channel.Reader.TryRead(out var evt)) batch.Add(evt);
                if (batch.Count > 0) await SendAsync(batch, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task SendAsync(IReadOnlyList<AiIdentityAccessEvent> events, CancellationToken ct)
    {
        var key = FirstNonEmpty(
            cfg["InternalApi:Key"],
            cfg["TaskForgeInternalApi:ApiKey"],
            cfg["TaskForge:InternalKey"],
            Environment.GetEnvironmentVariable("TASKFORGE_INTERNAL_KEY"));
        if (string.IsNullOrWhiteSpace(key)) return;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "api/internal/ai-access/events")
            {
                Content = JsonContent.Create(new AiIdentityAccessBatch(events))
            };
            request.Headers.TryAddWithoutValidation("X-Internal-Key", key);
            using var response = await clients.CreateClient("support-bot").SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Support-bot rejected AI identity telemetry. status={Status} count={Count}", (int)response.StatusCode, events.Count);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to send AI identity telemetry to support-bot.");
        }
    }

    private static string? ReferrerHost(string? value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri) ? Clamp(uri.Host, 128) : null;

    private static string? Clamp(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var clean = new string(value.Trim().Where(ch => !char.IsControl(ch) || ch == ' ').ToArray());
        return clean.Length <= max ? clean : clean[..max];
    }

    private static string? FirstNonEmpty(params string?[] values)
        => values.Select(value => (value ?? string.Empty).Trim()).FirstOrDefault(value => value.Length > 0);
}
