using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

internal static class TaskForgeDebugDiagnostics
{
    internal static bool Enabled => IsTruthy(Environment.GetEnvironmentVariable("TASKFORGE_DEBUG_LOGS"));

    public static IServiceCollection AddTaskForgeDebugDiagnostics(this IServiceCollection services, string serviceName)
    {
        if (Enabled)
        {
            Console.WriteLine($"[TFDBG BOOT] service={serviceName} logs=on utc={DateTimeOffset.UtcNow:O}");
        }
        return services;
    }

    public static IApplicationBuilder UseTaskForgeDebugRequestLogging(this IApplicationBuilder app, string serviceName)
    {
        if (!Enabled)
        {
            return app;
        }

        return app.Use(async (context, next) =>
        {
            var traceId = ExistingTraceId(context) ?? NewTraceId(serviceName);
            context.Response.Headers["X-TaskForge-Trace-Id"] = traceId;

            var logger = context.RequestServices
                .GetRequiredService<ILoggerFactory>()
                .CreateLogger("TaskForge.Debug.Runner");
            var started = Stopwatch.GetTimestamp();

            logger.LogInformation(
                "TFDBG RUNNER IN START trace={TraceId} service={Service} method={Method} path={Path} query={Query} remote={Remote} contentType={ContentType} contentLength={ContentLength}",
                traceId,
                serviceName,
                context.Request.Method,
                context.Request.Path.Value,
                context.Request.QueryString.Value,
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                context.Request.ContentType ?? "none",
                context.Request.ContentLength);

            try
            {
                await next();
            }
            catch (Exception ex)
            {
                logger.LogError(
                    ex,
                    "TFDBG RUNNER IN EXCEPTION trace={TraceId} service={Service} method={Method} path={Path} durationMs={DurationMs:F2}",
                    traceId,
                    serviceName,
                    context.Request.Method,
                    context.Request.Path.Value,
                    Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                throw;
            }
            finally
            {
                logger.LogInformation(
                    "TFDBG RUNNER IN END trace={TraceId} service={Service} method={Method} path={Path} status={Status} durationMs={DurationMs:F2} responseType={ResponseType} responseLength={ResponseLength}",
                    traceId,
                    serviceName,
                    context.Request.Method,
                    context.Request.Path.Value,
                    context.Response.StatusCode,
                    Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                    context.Response.ContentType ?? "none",
                    context.Response.ContentLength);
            }
        });
    }

    private static string? ExistingTraceId(HttpContext context)
        => context.Request.Headers["X-TaskForge-Trace-Id"].FirstOrDefault()
           ?? context.Request.Headers["X-Request-Id"].FirstOrDefault()
           ?? context.Request.Headers["X-Correlation-Id"].FirstOrDefault();

    private static string NewTraceId(string serviceName)
    {
        var raw = $"{serviceName}-{DateTimeOffset.UtcNow:HHmmssfff}-{Guid.NewGuid():N}";
        return raw[..Math.Min(48, raw.Length)];
    }

    private static bool IsTruthy(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        return normalized is "1" or "true" or "yes" or "on" or "debug";
    }
}
