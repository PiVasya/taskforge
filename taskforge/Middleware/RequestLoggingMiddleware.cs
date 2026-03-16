using System.Diagnostics;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using taskforge.Data;
using taskforge.Data.Models.Entities;

namespace taskforge.Middleware;

public sealed class RequestLoggingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<RequestLoggingMiddleware> _logger;

    public RequestLoggingMiddleware(RequestDelegate next, ILogger<RequestLoggingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, ApplicationDbContext db)
    {
        var path = context.Request.Path.ToString();
        if (!ShouldLog(path))
        {
            await _next(context);
            return;
        }

        var sw = Stopwatch.StartNew();
        try
        {
            await _next(context);
        }
        finally
        {
            sw.Stop();
            try
            {
                var userId = TryGetUserId(context.User);
                var log = new RequestLog
                {
                    UserId = userId,
                    Method = context.Request.Method,
                    Path = NormalizePath(path),
                    QueryString = string.IsNullOrWhiteSpace(context.Request.QueryString.Value) ? null : Trim(context.Request.QueryString.Value, 2048),
                    StatusCode = context.Response?.StatusCode ?? 0,
                    DurationMs = sw.ElapsedMilliseconds,
                    ClientType = DetectClientType(context, path),
                    IpAddress = context.Connection.RemoteIpAddress?.ToString(),
                    IsAuthenticated = context.User?.Identity?.IsAuthenticated == true,
                    UserAgent = Trim(context.Request.Headers.UserAgent.ToString(), 2048),
                    CreatedAtUtc = DateTime.UtcNow,
                };

                db.RequestLogs.Add(log);
                await db.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to persist request analytics log for path {Path}", path);
            }
        }
    }

    private static bool ShouldLog(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (!path.StartsWith("/api", StringComparison.OrdinalIgnoreCase)) return false;
        if (path.StartsWith("/api/private-files", StringComparison.OrdinalIgnoreCase)) return false;
        if (path.StartsWith("/api/public-files", StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    private static Guid? TryGetUserId(ClaimsPrincipal? user)
    {
        var raw = user?.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? user?.FindFirstValue("sub")
            ?? user?.FindFirstValue("nameid");

        return Guid.TryParse(raw, out var id) ? id : null;
    }

    private static string DetectClientType(HttpContext context, string path)
    {
        var ua = context.Request.Headers.UserAgent.ToString();
        if (path.StartsWith("/api/telegram", StringComparison.OrdinalIgnoreCase)) return "telegram";
        if (path.StartsWith("/api/admin", StringComparison.OrdinalIgnoreCase)) return "admin";
        if (path.Contains("minecraft", StringComparison.OrdinalIgnoreCase)) return "minecraft";
        if (context.Request.Headers.ContainsKey("X-Internal-Key")) return "internal";
        if (ua.Contains("Mozilla", StringComparison.OrdinalIgnoreCase)) return context.User?.Identity?.IsAuthenticated == true ? "web-auth" : "web-anon";
        if (ua.Contains("Java-http-client", StringComparison.OrdinalIgnoreCase)) return "java-client";
        if (ua.Contains("curl", StringComparison.OrdinalIgnoreCase) || ua.Contains("Postman", StringComparison.OrdinalIgnoreCase)) return "manual";
        return context.User?.Identity?.IsAuthenticated == true ? "api-auth" : "api-anon";
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "/";
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            if (Guid.TryParse(part, out _))
            {
                parts[i] = ":id";
                continue;
            }
            if (long.TryParse(part, out _) && part.Length >= 6)
            {
                parts[i] = ":num";
            }
        }
        return "/" + string.Join('/', parts);
    }

    private static string? Trim(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return value.Length <= max ? value : value[..max];
    }
}
