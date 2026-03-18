using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;
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
        Exception? pipelineError = null;
        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            pipelineError = ex;
            throw;
        }
        finally
        {
            sw.Stop();
            try
            {
                var userId = TryGetUserId(context.User);
                var normalizedPath = NormalizePath(path);
                var method = context.Request.Method;
                var statusCode = pipelineError == null ? (context.Response?.StatusCode ?? 0) : 500;
                var clientType = DetectClientType(context, path);
                var ip = context.Connection.RemoteIpAddress?.ToString();
                var isAuthenticated = context.User?.Identity?.IsAuthenticated == true;
                var userAgent = Trim(context.Request.Headers.UserAgent.ToString(), 2048);
                var createdAtUtc = DateTime.UtcNow;

                var requestLog = new RequestLog
                {
                    UserId = userId,
                    Method = method,
                    Path = normalizedPath,
                    QueryString = string.IsNullOrWhiteSpace(context.Request.QueryString.Value) ? null : Trim(context.Request.QueryString.Value, 2048),
                    StatusCode = statusCode,
                    DurationMs = sw.ElapsedMilliseconds,
                    ClientType = clientType,
                    IpAddress = ip,
                    IsAuthenticated = isAuthenticated,
                    UserAgent = userAgent,
                    CreatedAtUtc = createdAtUtc,
                };

                var actionShape = BuildActionShape(context, normalizedPath, method, statusCode, clientType, sw.ElapsedMilliseconds, pipelineError);
                var actionLog = new UserActionLog
                {
                    UserId = userId,
                    Category = actionShape.Category,
                    ActionType = actionShape.ActionType,
                    Source = actionShape.Source,
                    Method = method,
                    Path = normalizedPath,
                    Target = actionShape.Target,
                    Description = actionShape.Description,
                    StatusCode = statusCode,
                    MetadataJson = SerializeMetadata(new Dictionary<string, object?>
                    {
                        ["clientType"] = clientType,
                        ["durationMs"] = sw.ElapsedMilliseconds,
                        ["queryString"] = string.IsNullOrWhiteSpace(context.Request.QueryString.Value) ? null : context.Request.QueryString.Value,
                        ["error"] = pipelineError?.GetType().Name,
                    }),
                    IpAddress = ip,
                    IsAuthenticated = isAuthenticated,
                    UserAgent = userAgent,
                    CreatedAtUtc = createdAtUtc,
                };

                db.RequestLogs.Add(requestLog);
                db.UserActionLogs.Add(actionLog);
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

    private static string? SerializeMetadata(Dictionary<string, object?> meta)
    {
        var clean = meta.Where(x => x.Value != null).ToDictionary(x => x.Key, x => x.Value);
        return clean.Count == 0 ? null : JsonSerializer.Serialize(clean);
    }

    private static (string Category, string ActionType, string Source, string? Target, string Description) BuildActionShape(HttpContext context, string path, string method, int statusCode, string clientType, long durationMs, Exception? pipelineError)
    {
        string category;
        string actionType;
        string? target = path;
        string description;
        var lower = path.ToLowerInvariant();

        if (lower.StartsWith("/api/auth/login"))
        {
            category = "auth";
            actionType = statusCode < 400 ? "login.success" : "login.failed";
            description = statusCode < 400 ? "Вход в аккаунт" : "Неудачная попытка входа";
        }
        else if (lower.StartsWith("/api/activity/page-view"))
        {
            category = "navigation";
            actionType = "page.view";
            target = context.Request.Headers["X-TaskForge-Page"].FirstOrDefault() ?? path;
            description = $"Открыл страницу {target}";
        }
        else if (lower.Contains("/image-test/submit-code") || lower.Contains("/image-test/compare-code") || lower.Contains("/image-test/run-code"))
        {
            category = "image-test";
            actionType = lower.Contains("submit") ? "image.submit-code" : lower.Contains("compare") ? "image.compare-code" : "image.run-code";
            description = "Запуск или отправка кодовой image-задачи";
        }
        else if (lower.Contains("/solutions") || lower.Contains("submit"))
        {
            category = "solutions";
            actionType = method.Equals("POST", StringComparison.OrdinalIgnoreCase) ? "solution.submit" : "solution.view";
            description = method.Equals("POST", StringComparison.OrdinalIgnoreCase) ? "Отправка решения" : "Просмотр решений";
        }
        else if (lower.StartsWith("/api/admin/"))
        {
            category = "admin";
            actionType = method.Equals("GET", StringComparison.OrdinalIgnoreCase) ? "admin.view" : "admin.change";
            description = method.Equals("GET", StringComparison.OrdinalIgnoreCase) ? "Админ просмотр" : "Админ действие";
        }
        else if (lower.Contains("/support"))
        {
            category = "support";
            actionType = method.Equals("POST", StringComparison.OrdinalIgnoreCase) ? "support.change" : "support.view";
            description = method.Equals("POST", StringComparison.OrdinalIgnoreCase) ? "Действие в техподдержке" : "Просмотр тикетов техподдержки";
        }
        else if (lower.Contains("/assignments") || lower.Contains("/courses"))
        {
            category = "learning";
            actionType = method.Equals("GET", StringComparison.OrdinalIgnoreCase) ? "learning.view" : "learning.change";
            description = method.Equals("GET", StringComparison.OrdinalIgnoreCase) ? "Просмотр учебных данных" : "Изменение учебных данных";
        }
        else
        {
            category = "api";
            actionType = method.Equals("GET", StringComparison.OrdinalIgnoreCase) ? "api.read" : "api.write";
            description = method.Equals("GET", StringComparison.OrdinalIgnoreCase) ? "Чтение данных API" : "Изменение данных API";
        }

        if (pipelineError != null)
        {
            description = $"{description}. Ошибка: {pipelineError.GetType().Name}";
        }
        else if (statusCode >= 500)
        {
            description = $"{description}. Серверная ошибка";
        }
        else if (statusCode >= 400)
        {
            description = $"{description}. Ошибка запроса";
        }
        else if (durationMs >= 3000)
        {
            description = $"{description}. Долгий ответ";
        }

        return (category, actionType, clientType, target, description);
    }
}
