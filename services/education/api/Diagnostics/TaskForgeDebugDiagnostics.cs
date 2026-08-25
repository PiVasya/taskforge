using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
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

    public static IApplicationBuilder UseTaskForgeDebugRequestLogging(this IApplicationBuilder app, string serviceName)
    {
        if (!Enabled) return app;

        return app.Use(async (context, next) =>
        {
            if (context.WebSockets.IsWebSocketRequest)
            {
                await next();
                return;
            }

            var traceId = ExistingTraceId(context) ?? NewTraceId(serviceName);
            context.Response.Headers["X-TaskForge-Trace-Id"] = traceId;
            context.Items["TaskForgeTraceId"] = traceId;
            using var traceScope = TaskForgeDebugTrace.PushTraceId(traceId);

            var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("TaskForge.Debug.Inbound");
            var started = Stopwatch.GetTimestamp();
            var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? context.User.FindFirstValue("sub") ?? context.User.FindFirstValue("userId") ?? "anonymous";
            var role = context.User.FindFirstValue(ClaimTypes.Role) ?? context.User.FindFirstValue("role") ?? "none";
            var caller = context.Request.Headers["X-TaskForge-Caller-Service"].FirstOrDefault()
                ?? context.Request.Headers["X-Internal-Service"].FirstOrDefault()
                ?? context.Request.Headers["X-Forwarded-Host"].FirstOrDefault()
                ?? "external/browser";

            logger.LogInformation(
                "TFDBG IN START trace={TraceId} service={Service} caller={Caller} method={Method} path={Path} query={Query} endpoint={Endpoint} remote={Remote} user={UserId} role={Role} contentType={ContentType} contentLength={ContentLength}",
                traceId,
                serviceName,
                caller,
                context.Request.Method,
                context.Request.Path.Value,
                context.Request.QueryString.Value,
                context.GetEndpoint()?.DisplayName ?? "<unmatched>",
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                Short(userId),
                role,
                context.Request.ContentType ?? "none",
                context.Request.ContentLength);

            var requestBody = await TryReadRequestBodyAsync(context.Request, context.RequestAborted);
            if (!string.IsNullOrWhiteSpace(requestBody))
            {
                logger.LogInformation("TFDBG IN BODY trace={TraceId} service={Service} path={Path} body={Body}", traceId, serviceName, context.Request.Path.Value, requestBody);
                var summary = TaskForgeDebugPayloadSummary.FromJsonLike(requestBody);
                if (summary.HasAnything)
                {
                    logger.LogInformation("TFDBG IN DATA trace={TraceId} service={Service} path={Path} ids={Ids} names={Names} emails={Emails} titles={Titles} placeholders={Placeholders}", traceId, serviceName, context.Request.Path.Value, summary.IdsForLog, summary.NamesForLog, summary.EmailsForLog, summary.TitlesForLog, summary.PlaceholdersForLog);
                }
            }

            var captureResponse = ShouldCaptureResponse(context.Request.Path.Value, context.Request.ContentType);
            var originalBody = context.Response.Body;
            var responseBuffer = captureResponse ? new MemoryStream() : null;
            if (captureResponse && responseBuffer is not null)
            {
                context.Response.Body = responseBuffer;
            }

            try
            {
                await next();
            }
            catch (Exception ex)
            {
                var elapsedOnError = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                logger.LogError(ex, "TFDBG IN EXCEPTION trace={TraceId} service={Service} method={Method} path={Path} durationMs={DurationMs:F2}", traceId, serviceName, context.Request.Method, context.Request.Path.Value, elapsedOnError);
                throw;
            }
            finally
            {
                var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                string? responseText = null;
                TaskForgeDebugPayloadSummary? responseSummary = null;

                if (captureResponse && responseBuffer is not null)
                {
                    responseBuffer.Position = 0;
                    responseText = await TryReadResponseBodyAsync(responseBuffer, context.Response.ContentType, context.RequestAborted);
                    responseBuffer.Position = 0;
                    await responseBuffer.CopyToAsync(originalBody, context.RequestAborted);
                    context.Response.Body = originalBody;
                    if (!string.IsNullOrWhiteSpace(responseText))
                    {
                        responseSummary = TaskForgeDebugPayloadSummary.FromJsonLike(responseText);
                    }
                }

                var userAfter = context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? context.User.FindFirstValue("sub") ?? context.User.FindFirstValue("userId") ?? userId;
                var roleAfter = context.User.FindFirstValue(ClaimTypes.Role) ?? context.User.FindFirstValue("role") ?? role;
                logger.LogInformation(
                    "TFDBG IN END trace={TraceId} service={Service} caller={Caller} method={Method} path={Path} status={Status} durationMs={DurationMs:F2} user={UserId} role={Role} responseType={ResponseType} responseLength={ResponseLength}",
                    traceId,
                    serviceName,
                    caller,
                    context.Request.Method,
                    context.Request.Path.Value,
                    context.Response.StatusCode,
                    elapsed,
                    Short(userAfter),
                    roleAfter,
                    context.Response.ContentType ?? "none",
                    context.Response.ContentLength);

                if (!string.IsNullOrWhiteSpace(responseText))
                {
                    logger.LogInformation("TFDBG IN RESPONSE trace={TraceId} service={Service} path={Path} body={Body}", traceId, serviceName, context.Request.Path.Value, responseText);
                }

                if (responseSummary?.HasAnything == true)
                {
                    logger.LogInformation("TFDBG IN RESPONSE-DATA trace={TraceId} service={Service} path={Path} ids={Ids} names={Names} emails={Emails} titles={Titles} placeholders={Placeholders}", traceId, serviceName, context.Request.Path.Value, responseSummary.IdsForLog, responseSummary.NamesForLog, responseSummary.EmailsForLog, responseSummary.TitlesForLog, responseSummary.PlaceholdersForLog);
                }

                if (responseBuffer is not null)
                {
                    await responseBuffer.DisposeAsync();
                }
            }
        });
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
        s = Regex.Replace(s, "(?i)(\\\"?(password|token|accessToken|refreshToken|authorization|secret|internalKey|apiKey|x-internal-key)\\\"?\\s*[:=]\\s*)\\\"?[^\\\",} ]+\\\"?", "$1\"[redacted]\"");
        if (s.Length > MaxLoggedBodyChars) s = s[..MaxLoggedBodyChars] + $"...<trimmed {s.Length - MaxLoggedBodyChars} chars>";
        return s;
    }

    private static string? ExistingTraceId(HttpContext context)
    {
        var headers = context.Request.Headers;
        return headers["X-TaskForge-Trace-Id"].FirstOrDefault()
            ?? headers["X-TaskForge-Front-Trace-Id"].FirstOrDefault()
            ?? headers["X-Request-ID"].FirstOrDefault()
            ?? headers["X-TaskForge-Gateway-Request-Id"].FirstOrDefault();
    }

    private static bool ShouldCaptureResponse(string? path, string? contentType)
    {
        var p = (path ?? string.Empty).ToLowerInvariant();
        if (p.Contains("/hubs") || p.Contains("/hub")) return false;
        if (p.Contains("/api/files") || p.Contains("/files/")) return false;
        var ct = (contentType ?? string.Empty).ToLowerInvariant();
        if (ct.Contains("multipart") || ct.Contains("octet-stream")) return false;
        return true;
    }

    private static async Task<string?> TryReadRequestBodyAsync(HttpRequest request, CancellationToken ct)
    {
        if (request.ContentLength is null or <= 0 || request.Body is null) return null;
        if (request.ContentType?.Contains("multipart", StringComparison.OrdinalIgnoreCase) == true) return $"<multipart contentLength={request.ContentLength}>";
        if (request.ContentType?.Contains("octet-stream", StringComparison.OrdinalIgnoreCase) == true) return $"<binary contentLength={request.ContentLength}>";
        try
        {
            request.EnableBuffering(bufferThreshold: 64 * 1024, bufferLimit: 8 * 1024 * 1024);
            using var reader = new StreamReader(request.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
            var text = await reader.ReadToEndAsync(ct);
            request.Body.Position = 0;
            return Redact(text);
        }
        catch (Exception ex)
        {
            return $"<body-read-failed {ex.GetType().Name}: {ex.Message}>";
        }
    }

    private static async Task<string?> TryReadResponseBodyAsync(Stream stream, string? contentType, CancellationToken ct)
    {
        if (stream.Length <= 0) return null;
        var ctLower = (contentType ?? string.Empty).ToLowerInvariant();
        if (ctLower.Contains("octet-stream") || ctLower.Contains("image/") || ctLower.Contains("zip") || ctLower.Contains("pdf")) return $"<binary response length={stream.Length}>";
        try
        {
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
            var text = await reader.ReadToEndAsync(ct);
            return Redact(text);
        }
        catch (Exception ex)
        {
            return $"<response-read-failed {ex.GetType().Name}: {ex.Message}>";
        }
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
            if (TaskForgeDebugDiagnostics.Enabled)
            {
                builder.AdditionalHandlers.Add(_services.GetRequiredService<TaskForgeDebugHttpHandler>());
            }
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
        var traceId = request.Headers.TryGetValues("X-TaskForge-Trace-Id", out var existing)
            ? existing.FirstOrDefault() ?? TaskForgeDebugTrace.CurrentTraceId ?? TaskForgeDebugDiagnostics.NewTraceId(_serviceName.Value)
            : TaskForgeDebugTrace.CurrentTraceId ?? TaskForgeDebugDiagnostics.NewTraceId(_serviceName.Value);
        request.Headers.Remove("X-TaskForge-Trace-Id");
        request.Headers.TryAddWithoutValidation("X-TaskForge-Trace-Id", traceId);
        request.Headers.TryAddWithoutValidation("X-TaskForge-Caller-Service", _serviceName.Value);

        var body = await TryReadHttpContentAsync(request.Content, cancellationToken);
        var requestSummary = TaskForgeDebugPayloadSummary.FromJsonLike(body);
        _logger.LogInformation(
            "TFDBG OUT START trace={TraceId} caller={Caller} target={Target} method={Method} url={Url} body={Body}",
            traceId,
            _serviceName.Value,
            request.RequestUri?.Host ?? "unknown",
            request.Method.Method,
            request.RequestUri?.ToString(),
            body);
        if (requestSummary.HasAnything)
        {
            _logger.LogInformation("TFDBG OUT DATA trace={TraceId} caller={Caller} target={Target} ids={Ids} names={Names} emails={Emails} titles={Titles}", traceId, _serviceName.Value, request.RequestUri?.Host ?? "unknown", requestSummary.IdsForLog, requestSummary.NamesForLog, requestSummary.EmailsForLog, requestSummary.TitlesForLog);
        }

        var started = Stopwatch.GetTimestamp();
        try
        {
            var response = await base.SendAsync(request, cancellationToken);
            var responseBody = await TryReadHttpContentAsync(response.Content, cancellationToken);
            var responseSummary = TaskForgeDebugPayloadSummary.FromJsonLike(responseBody);
            var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            _logger.LogInformation(
                "TFDBG OUT END trace={TraceId} caller={Caller} target={Target} method={Method} url={Url} status={Status} durationMs={DurationMs:F2} body={Body}",
                traceId,
                _serviceName.Value,
                request.RequestUri?.Host ?? "unknown",
                request.Method.Method,
                request.RequestUri?.ToString(),
                (int)response.StatusCode,
                elapsed,
                responseBody);
            if (responseSummary.HasAnything)
            {
                _logger.LogInformation("TFDBG OUT RESPONSE-DATA trace={TraceId} caller={Caller} target={Target} ids={Ids} names={Names} emails={Emails} titles={Titles} placeholders={Placeholders}", traceId, _serviceName.Value, request.RequestUri?.Host ?? "unknown", responseSummary.IdsForLog, responseSummary.NamesForLog, responseSummary.EmailsForLog, responseSummary.TitlesForLog, responseSummary.PlaceholdersForLog);
            }
            if (IsUserSummaryCall(request) && requestSummary.GuidCount > 0)
            {
                var missing = System.Math.Max(0, requestSummary.GuidCount - responseSummary.GuidCount);
                if (responseSummary.NamesCount == 0 || missing > 0 || responseSummary.PlaceholderCount > 0)
                {
                    _logger.LogWarning(
                        "TFDBG OUT SEMANTIC-MISMATCH trace={TraceId} caller={Caller} expected=user summaries requestedIds={RequestedIds} returnedIds={ReturnedIds} returnedNames={ReturnedNames} placeholders={Placeholders} message={Message}",
                        traceId,
                        _serviceName.Value,
                        requestSummary.GuidCount,
                        responseSummary.GuidCount,
                        responseSummary.NamesCount,
                        responseSummary.PlaceholderCount,
                        "Сервис попросил identity-api раскрыть пользователей, но в ответе нет полного набора нормальных имён/email. Вот здесь ловим 'попросил Торопа Валерия, получил НЕ Торопа Валерия'.");
                }
            }
            return response;
        }
        catch (Exception ex)
        {
            var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            _logger.LogError(ex, "TFDBG OUT EXCEPTION trace={TraceId} caller={Caller} method={Method} url={Url} durationMs={DurationMs:F2}", traceId, _serviceName.Value, request.Method.Method, request.RequestUri?.ToString(), elapsed);
            throw;
        }
    }

    private static bool IsUserSummaryCall(HttpRequestMessage request)
        => request.RequestUri?.AbsolutePath.Contains("/api/internal/users/summaries", StringComparison.OrdinalIgnoreCase) == true;

    private static async Task<string?> TryReadHttpContentAsync(HttpContent? content, CancellationToken ct)
    {
        if (content is null) return null;
        var media = content.Headers.ContentType?.MediaType ?? string.Empty;
        if (media.Contains("octet-stream", StringComparison.OrdinalIgnoreCase) || media.Contains("multipart", StringComparison.OrdinalIgnoreCase))
        {
            return $"<{media} length={content.Headers.ContentLength?.ToString() ?? "unknown"}>";
        }
        try
        {
            await content.LoadIntoBufferAsync(8 * 1024 * 1024);
            var text = await content.ReadAsStringAsync(ct);
            return TaskForgeDebugDiagnostics.Redact(text);
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
    private readonly HashSet<string> _titles = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _placeholders = new(StringComparer.OrdinalIgnoreCase);

    public int GuidCount => _ids.Count;
    public int NamesCount => _names.Count;
    public int PlaceholderCount => _placeholders.Count;
    public bool HasAnything => _ids.Count + _names.Count + _emails.Count + _titles.Count + _placeholders.Count > 0;
    public string IdsForLog => Join(_ids, 24);
    public string NamesForLog => Join(_names, 16);
    public string EmailsForLog => Join(_emails, 12);
    public string TitlesForLog => Join(_titles, 12);
    public string PlaceholdersForLog => Join(_placeholders, 12);

    public static TaskForgeDebugPayloadSummary FromJsonLike(string? text)
    {
        var summary = new TaskForgeDebugPayloadSummary();
        if (string.IsNullOrWhiteSpace(text)) return summary;

        foreach (Match m in Regex.Matches(text, "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}"))
        {
            summary._ids.Add(TaskForgeDebugDiagnostics.Short(m.Value));
        }

        try
        {
            using var doc = JsonDocument.Parse(text);
            summary.Visit(doc.RootElement, null);
        }
        catch
        {
            if (text.Contains("Пользователь", StringComparison.OrdinalIgnoreCase)) summary._placeholders.Add("Пользователь");
        }
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
                if (prop.Contains("title") || prop.Contains("subject")) _titles.Add(value);
                if (prop.Contains("name") || prop.Contains("display") || prop.Contains("full") || prop.Contains("first") || prop.Contains("last"))
                {
                    if (string.Equals(value, "Пользователь", StringComparison.OrdinalIgnoreCase) || value.StartsWith("Пользователь #", StringComparison.OrdinalIgnoreCase)) _placeholders.Add(value);
                    else _names.Add(value);
                }
                if (value.Contains("Пользователь #", StringComparison.OrdinalIgnoreCase)) _placeholders.Add(value);
                break;
        }
    }

    private static string Join(HashSet<string> values, int take)
    {
        if (values.Count == 0) return "-";
        var arr = values.Take(take).ToArray();
        return string.Join(" | ", arr) + (values.Count > arr.Length ? $" | +{values.Count - arr.Length}" : string.Empty);
    }
}

internal static class TaskForgeDebugTrace
{
    private static readonly AsyncLocal<string?> TraceIdContext = new();

    internal static bool Enabled => TaskForgeDebugDiagnostics.Enabled;
    internal static string? CurrentTraceId => TraceIdContext.Value;

    internal static IDisposable PushTraceId(string? traceId)
    {
        var previous = TraceIdContext.Value;
        TraceIdContext.Value = string.IsNullOrWhiteSpace(traceId) ? null : traceId.Trim();
        return new TraceScope(previous);
    }

    public static void Map(string action, params (string Key, object? Value)[] fields)
    {
        if (!Enabled) return;
        var details = fields.Length == 0
            ? string.Empty
            : " " + string.Join(" ", fields.Select(field => $"{MapKey(field.Key)}={MapValue(field.Value)}"));
        Console.WriteLine($"[TFDBG MAP {MapKey(action)}] trace={MapValue(CurrentTraceId ?? "none")}{details} utc={DateTimeOffset.UtcNow:O}");
    }

    public static string MapList<T>(IEnumerable<T>? values, int take = 120)
    {
        if (values is null) return "-";
        var rows = values
            .Select(value => value is null ? null : value.ToString()?.Trim())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .Distinct(StringComparer.Ordinal)
            .Take(System.Math.Max(1, take) + 1)
            .ToArray();
        if (rows.Length == 0) return "-";
        if (rows.Length <= take) return string.Join("|", rows);
        return string.Join("|", rows.Take(take)) + $"|...+{rows.Length - take}";
    }

    public static string Fingerprint(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "none";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash.AsSpan(0, 12)).ToLowerInvariant();
    }

    private static string MapKey(string? value)
    {
        var raw = (value ?? string.Empty).Trim();
        if (raw.Length == 0) return "EVENT";
        var chars = raw.Select(ch => char.IsLetterOrDigit(ch) || ch is '_' or '-' ? char.ToUpperInvariant(ch) : '_').ToArray();
        return new string(chars);
    }

    private static string MapValue(object? value)
    {
        if (value is null) return "-";
        if (value is bool boolean) return boolean ? "true" : "false";
        if (value is DateTimeOffset dto) return dto.ToString("O");
        if (value is DateTime dt) return dt.ToUniversalTime().ToString("O");
        var raw = TaskForgeDebugDiagnostics.Redact(value.ToString()).Trim();
        if (raw.Length == 0) return "-";
        if (raw.Length > 1200) raw = raw[..1200] + $"...<trimmed {raw.Length - 1200}>";
        return raw.Any(char.IsWhiteSpace) || raw.Contains('=') || raw.Contains('"')
            ? JsonSerializer.Serialize(raw)
            : raw;
    }

    private sealed class TraceScope : IDisposable
    {
        private readonly string? _previous;
        private bool _disposed;

        public TraceScope(string? previous) => _previous = previous;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            TraceIdContext.Value = _previous;
        }
    }

    public static void ServiceBoot(string serviceName)
    {
        if (!Enabled) return;
        Console.WriteLine($"[TFDBG BOOT] service={serviceName} logs=on utc={DateTimeOffset.UtcNow:O}");
    }

    public static void UserSummaryRequest(string serviceName, string target, IEnumerable<Guid> ids)
    {
        if (!Enabled) return;
        var arr = ids.Where(x => x != Guid.Empty).Distinct().Take(40).Select(x => TaskForgeDebugDiagnostics.Short(x.ToString())).ToArray();
        Console.WriteLine($"[TFDBG USERS ASK] service={serviceName} target={target} count={arr.Length} ids={string.Join("|", arr)}");
    }

    public static void UserSummaryResponse<T>(string serviceName, string target, IReadOnlyCollection<Guid> requestedIds, IReadOnlyDictionary<Guid, T> users)
    {
        if (!Enabled) return;
        var found = users.Keys.ToHashSet();
        var missing = requestedIds.Where(x => x != Guid.Empty && !found.Contains(x)).Take(40).Select(x => TaskForgeDebugDiagnostics.Short(x.ToString())).ToArray();
        var names = users.Values.Take(20).Select(ExtractUserName).Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
        Console.WriteLine($"[TFDBG USERS GOT] service={serviceName} target={target} requested={requestedIds.Count} found={users.Count} missing={missing.Length} names={string.Join("|", names)} missingIds={string.Join("|", missing)}");
        if (requestedIds.Count > 0 && (users.Count == 0 || missing.Length > 0 || names.Length == 0))
        {
            Console.WriteLine($"[TFDBG USERS WRONG] service={serviceName} target={target} message=asked_identity_for_real_user_names_but_mapping_is_incomplete_or_placeholder requested={requestedIds.Count} found={users.Count} names={names.Length}");
        }
    }

    public static void UserSummaryServed<T>(string serviceName, IReadOnlyCollection<Guid> requestedIds, IReadOnlyCollection<T> users)
    {
        if (!Enabled) return;
        var names = users.Take(30).Select(ExtractUserName).Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
        Console.WriteLine($"[TFDBG USERS SERVE] service={serviceName} requester=internal-users-summaries requested={requestedIds.Count} returned={users.Count} names={string.Join("|", names)}");
        if (requestedIds.Count > users.Count)
        {
            var foundIds = users.Select(ExtractUserId).Where(x => x.HasValue).Select(x => x!.Value).ToHashSet();
            var missing = requestedIds.Where(x => !foundIds.Contains(x)).Take(40).Select(x => TaskForgeDebugDiagnostics.Short(x.ToString())).ToArray();
            Console.WriteLine($"[TFDBG USERS MISSING] service={serviceName} requested={requestedIds.Count} returned={users.Count} missingIds={string.Join("|", missing)}");
        }
    }

    private static string? ExtractUserName<T>(T value)
    {
        if (value is null) return null;
        var type = value.GetType();
        string? Get(params string[] names)
        {
            foreach (var name in names)
            {
                var prop = type.GetProperty(name);
                var raw = prop?.GetValue(value)?.ToString()?.Trim();
                if (!string.IsNullOrWhiteSpace(raw)) return raw;
            }
            return null;
        }
        var explicitName = Get("DisplayName", "FullName", "displayName", "fullName");
        if (!string.IsNullOrWhiteSpace(explicitName)) return explicitName;
        var first = Get("FirstName", "firstName");
        var last = Get("LastName", "lastName");
        var full = string.Join(' ', new[] { first, last }.Where(x => !string.IsNullOrWhiteSpace(x))).Trim();
        return string.IsNullOrWhiteSpace(full) ? Get("Email", "email", "MaskedEmail", "maskedEmail") : full;
    }

    private static Guid? ExtractUserId<T>(T value)
    {
        if (value is null) return null;
        var type = value.GetType();
        foreach (var name in new[] { "UserId", "userId", "Id", "id" })
        {
            var raw = type.GetProperty(name)?.GetValue(value);
            if (raw is Guid g) return g;
            if (Guid.TryParse(raw?.ToString(), out var parsed)) return parsed;
        }
        return null;
    }
}
