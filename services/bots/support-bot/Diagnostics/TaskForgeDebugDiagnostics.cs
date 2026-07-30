using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;

internal static class TaskForgeDebugDiagnostics
{
    internal const int MaxLoggedBodyChars = 4000;
    internal static bool Enabled => IsTruthy(Environment.GetEnvironmentVariable("TASKFORGE_DEBUG_LOGS"));

    public static IServiceCollection AddTaskForgeDebugDiagnostics(this IServiceCollection services, string serviceName)
    {
        services.TryAddSingleton(new TaskForgeDebugServiceName(serviceName));
        services.TryAddTransient<TaskForgeDebugHttpHandler>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHttpMessageHandlerBuilderFilter, TaskForgeDebugHttpMessageHandlerBuilderFilter>());
        TaskForgeDebugTrace.ServiceBoot(serviceName);
        return services;
    }

    internal static bool IsTruthy(string? value)
    {
        var v = (value ?? string.Empty).Trim().ToLowerInvariant();
        return v is "1" or "true" or "yes" or "on" or "debug";
    }

    internal static string NewTraceId(string serviceName)
    {
        var raw = $"{serviceName}-{DateTimeOffset.UtcNow:HHmmssfff}-{Guid.NewGuid():N}";
        return raw[..System.Math.Min(48, raw.Length)];
    }

    internal static string Short(string? value)
    {
        var s = (value ?? string.Empty).Trim();
        if (s.Length <= 12) return string.IsNullOrWhiteSpace(s) ? "none" : s;
        return s[..8] + "..." + s[^4..];
    }

    internal static string Redact(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var s = value.Replace("\r", " ").Replace("\n", " ");
        s = Regex.Replace(s, "(?i)(\\\"?(?:[a-z0-9_]*(?:password|token|secret|internalkey|apikey|verificationcode|recoverycode)[a-z0-9_]*|authorization|x-internal-key)\\\"?\\s*[:=]\\s*)\\\"?[^\\\",} ]+\\\"?", "$1\"[redacted]\"");
        if (s.Length > MaxLoggedBodyChars) s = s[..MaxLoggedBodyChars] + $"...<trimmed {s.Length - MaxLoggedBodyChars} chars>";
        return s;
    }
}

internal sealed record TaskForgeDebugServiceName(string Value);

internal sealed class TaskForgeDebugHttpMessageHandlerBuilderFilter : IHttpMessageHandlerBuilderFilter
{
    private readonly IServiceProvider _services;
    public TaskForgeDebugHttpMessageHandlerBuilderFilter(IServiceProvider services) => _services = services;
    public Action<HttpMessageHandlerBuilder> Configure(Action<HttpMessageHandlerBuilder> next)
    {
        return builder =>
        {
            next(builder);
            if (TaskForgeDebugDiagnostics.Enabled) builder.AdditionalHandlers.Add(_services.GetRequiredService<TaskForgeDebugHttpHandler>());
        };
    }
}

internal sealed class TaskForgeDebugHttpHandler : DelegatingHandler
{
    private readonly ILogger<TaskForgeDebugHttpHandler> _logger;
    private readonly TaskForgeDebugServiceName _serviceName;
    public TaskForgeDebugHttpHandler(ILogger<TaskForgeDebugHttpHandler> logger, TaskForgeDebugServiceName serviceName)
    {
        _logger = logger;
        _serviceName = serviceName;
    }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var traceId = TaskForgeDebugDiagnostics.NewTraceId(_serviceName.Value);
        request.Headers.TryAddWithoutValidation("X-TaskForge-Trace-Id", traceId);
        request.Headers.TryAddWithoutValidation("X-TaskForge-Caller-Service", _serviceName.Value);
        var body = await ReadAsync(request.Content, cancellationToken);
        var requestSummary = TaskForgeDebugPayloadSummary.FromJsonLike(body);
        _logger.LogInformation("TFDBG OUT START trace={TraceId} caller={Caller} method={Method} url={Url} ids={Ids} body={Body}", traceId, _serviceName.Value, request.Method.Method, request.RequestUri?.ToString(), requestSummary.IdsForLog, body);
        var started = Stopwatch.GetTimestamp();
        try
        {
            var response = await base.SendAsync(request, cancellationToken);
            var responseBody = await ReadAsync(response.Content, cancellationToken);
            var responseSummary = TaskForgeDebugPayloadSummary.FromJsonLike(responseBody);
            _logger.LogInformation("TFDBG OUT END trace={TraceId} caller={Caller} method={Method} url={Url} status={Status} durationMs={DurationMs:F2} ids={Ids} names={Names} emails={Emails} body={Body}", traceId, _serviceName.Value, request.Method.Method, request.RequestUri?.ToString(), (int)response.StatusCode, Stopwatch.GetElapsedTime(started).TotalMilliseconds, responseSummary.IdsForLog, responseSummary.NamesForLog, responseSummary.EmailsForLog, responseBody);
            if (request.RequestUri?.AbsolutePath.Contains("/api/internal/users/summaries", StringComparison.OrdinalIgnoreCase) == true && requestSummary.GuidCount > 0 && responseSummary.NamesCount == 0)
            {
                _logger.LogWarning("TFDBG OUT SEMANTIC-MISMATCH trace={TraceId} caller={Caller} requestedIds={RequestedIds} returnedNames=0 message=asked_identity_for_user_names_but_got_no_names", traceId, _serviceName.Value, requestSummary.GuidCount);
            }
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TFDBG OUT EXCEPTION trace={TraceId} caller={Caller} method={Method} url={Url} durationMs={DurationMs:F2}", traceId, _serviceName.Value, request.Method.Method, request.RequestUri?.ToString(), Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            throw;
        }
    }
    private static async Task<string?> ReadAsync(HttpContent? content, CancellationToken ct)
    {
        if (content is null) return null;
        try
        {
            await content.LoadIntoBufferAsync(8 * 1024 * 1024);
            return TaskForgeDebugDiagnostics.Redact(await content.ReadAsStringAsync(ct));
        }
        catch (Exception ex)
        {
            return $"<content-read-failed {ex.GetType().Name}: {ex.Message}>";
        }
    }
}

internal sealed class TaskForgeDebugPayloadSummary
{
    private readonly HashSet<string> _ids = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _names = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _emails = new(StringComparer.OrdinalIgnoreCase);
    public int GuidCount => _ids.Count;
    public int NamesCount => _names.Count;
    public bool HasAnything => _ids.Count + _names.Count + _emails.Count > 0;
    public string IdsForLog => Join(_ids, 24);
    public string NamesForLog => Join(_names, 16);
    public string EmailsForLog => Join(_emails, 12);
    public static TaskForgeDebugPayloadSummary FromJsonLike(string? text)
    {
        var summary = new TaskForgeDebugPayloadSummary();
        if (string.IsNullOrWhiteSpace(text)) return summary;
        foreach (Match m in Regex.Matches(text, "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")) summary._ids.Add(TaskForgeDebugDiagnostics.Short(m.Value));
        try
        {
            using var doc = JsonDocument.Parse(text);
            summary.Visit(doc.RootElement, null);
        }
        catch { }
        return summary;
    }
    private void Visit(JsonElement element, string? property)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in element.EnumerateObject()) Visit(p.Value, p.Name);
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray()) Visit(item, property);
                break;
            case JsonValueKind.String:
                var value = element.GetString()?.Trim();
                if (string.IsNullOrWhiteSpace(value)) return;
                var prop = (property ?? string.Empty).ToLowerInvariant();
                if (Guid.TryParse(value, out _)) _ids.Add(TaskForgeDebugDiagnostics.Short(value));
                if (prop.Contains("email")) _emails.Add(value);
                if (prop.Contains("name") || prop.Contains("display") || prop.Contains("full") || prop.Contains("first") || prop.Contains("last")) _names.Add(value);
                break;
        }
    }
    private static string Join(HashSet<string> values, int take) => values.Count == 0 ? "-" : string.Join(" | ", values.Take(take));
}

internal static class TaskForgeDebugTrace
{
    internal static bool Enabled => TaskForgeDebugDiagnostics.Enabled;
    public static void ServiceBoot(string serviceName)
    {
        if (Enabled) Console.WriteLine($"[TFDBG BOOT] service={serviceName} logs=on utc={DateTimeOffset.UtcNow:O}");
    }
}
