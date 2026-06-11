using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

public static class TaskForgeRequestSecurity
{
    private enum Requirement
    {
        Public,
        Authenticated,
        Editor,
        Admin,
        Internal
    }

    public static IApplicationBuilder UseTaskForgeRequestSecurity(this IApplicationBuilder app, string service)
    {
        return app.Use(async (context, next) =>
        {
            var requirement = GetRequirement(service, context.Request.Method, Normalize(context.Request.Path));
            if (requirement == Requirement.Public)
            {
                await next();
                return;
            }

            if (HttpMethods.IsOptions(context.Request.Method))
            {
                context.Response.StatusCode = StatusCodes.Status204NoContent;
                return;
            }

            var config = context.RequestServices.GetRequiredService<IConfiguration>();

            if (requirement == Requirement.Internal)
            {
                if (!IsInternalRequest(context, config))
                {
                    await WriteProblem(context, StatusCodes.Status404NotFound,
                        "Ресурс не найден.",
                        "NOT_FOUND");
                    return;
                }

                await next();
                return;
            }

            var principal = ValidateUser(context, config);
            if (principal is null)
            {
                await WriteProblem(context, StatusCodes.Status401Unauthorized,
                    "Сессия истекла или вы не вошли в систему.",
                    "AUTH_REQUIRED");
                return;
            }

            if (requirement == Requirement.Admin && !HasAnyRole(principal, "Admin"))
            {
                await WriteProblem(context, StatusCodes.Status403Forbidden,
                    "У вас нет доступа к этому разделу.",
                    "ADMIN_REQUIRED");
                return;
            }

            if (requirement == Requirement.Editor && !HasAnyRole(principal, "Admin", "Editor", "LearningEditor"))
            {
                await WriteProblem(context, StatusCodes.Status403Forbidden,
                    "Для этого действия нужны права редактора.",
                    "EDITOR_REQUIRED");
                return;
            }

            context.User = principal;
            await next();
        });
    }

    private static Requirement GetRequirement(string service, string method, string path)
    {
        if (path is "" or "/" || path.StartsWith("/health") || path.StartsWith("/swagger")) return Requirement.Public;
        if (!path.StartsWith("/api") && !path.StartsWith("/hubs")) return Requirement.Public;
        if (path.Contains("/schema-owner")) return Requirement.Admin;
        if (path.StartsWith("/api/internal/")) return Requirement.Internal;
        if (string.Equals(service, "content", StringComparison.OrdinalIgnoreCase) && path.StartsWith("/api/admin/learning")) return Requirement.Editor;
        if (string.Equals(service, "quiz", StringComparison.OrdinalIgnoreCase) && path.StartsWith("/api/admin/quiz")) return Requirement.Editor;
        if (string.Equals(service, "tasks", StringComparison.OrdinalIgnoreCase) && path.StartsWith("/api/admin/assignments")) return Requirement.Editor;
        if (path.StartsWith("/api/admin/")) return Requirement.Admin;
        if (path.StartsWith("/hubs/")) return Requirement.Authenticated;

        var safeMethod = HttpMethods.IsGet(method) || HttpMethods.IsHead(method);
        var writeMethod = HttpMethods.IsPost(method) || HttpMethods.IsPut(method) || HttpMethods.IsPatch(method) || HttpMethods.IsDelete(method);

        if (path.StartsWith("/api/auth/login") || path.StartsWith("/api/auth/register") || path.StartsWith("/api/auth/refresh") || path.StartsWith("/api/auth/logout")) return Requirement.Public;
        if (path.StartsWith("/api/users/") && path.EndsWith("/public-profile")) return Requirement.Public;
        if (path == "/api/system-status") return Requirement.Public;
        if (path == "/api/activity/page-view" && HttpMethods.IsPost(method)) return Requirement.Public;

        if (path.StartsWith("/api/profile") || path.StartsWith("/api/me/") || path.StartsWith("/api/integrations/")) return Requirement.Authenticated;
        if (path.StartsWith("/api/notifications")) return Requirement.Authenticated;
        if (path.StartsWith("/api/private-files")) return Requirement.Authenticated;
        if (path.StartsWith("/api/files/images")) return Requirement.Authenticated;
        if (path == "/api/files") return Requirement.Authenticated;
        if (path.StartsWith("/api/files/")) return safeMethod ? Requirement.Public : Requirement.Authenticated;
        if (path.StartsWith("/api/support")) return Requirement.Authenticated;
        if (path.StartsWith("/api/agent")) return Requirement.Authenticated;
        if (path.StartsWith("/api/telegram")) return Requirement.Internal;

        if (path.StartsWith("/api/learning")) return safeMethod ? Requirement.Public : Requirement.Editor;
        if (path.StartsWith("/api/quiz")) return path.StartsWith("/api/quiz/me") || writeMethod ? Requirement.Authenticated : Requirement.Public;

        if (path == "/api/courses" || path.StartsWith("/api/courses/")) return safeMethod ? Requirement.Public : Requirement.Editor;
        if (path == "/api/groups" || path.StartsWith("/api/groups/")) return safeMethod ? Requirement.Public : Requirement.Admin;
        if (path.StartsWith("/api/assignments/") && path.EndsWith("/submit")) return Requirement.Authenticated;
        if (path.StartsWith("/api/assignments/") && path.EndsWith("/top-solutions")) return Requirement.Authenticated;
        if (path.StartsWith("/api/assignments/") && path.EndsWith("/edit")) return Requirement.Editor;
        if (path.StartsWith("/api/assignments/") && path.Contains("/image-test/reference")) return Requirement.Editor;
        if (path.StartsWith("/api/assignments/") && path.Contains("/image-test")) return Requirement.Authenticated;
        if (path.StartsWith("/api/assignments")) return writeMethod ? Requirement.Editor : Requirement.Authenticated;
        if (path.StartsWith("/api/task-tests") || path.StartsWith("/api/math-tasks")) return path.EndsWith("/edit") ? Requirement.Editor : Requirement.Authenticated;
        if (path.StartsWith("/api/tests")) return Requirement.Editor;

        if (path.StartsWith("/api/compiler") || path.StartsWith("/api/execution") || path.StartsWith("/api/image-runners")) return Requirement.Editor;
        if (path.Contains("/image-test/reference")) return Requirement.Editor;
        if (path.Contains("/image-test")) return Requirement.Authenticated;

        if (path.StartsWith("/api/leaderboard")) return Requirement.Public;
        if (path.StartsWith("/api/badges")) return safeMethod || path.Contains("/user/") ? Requirement.Public : Requirement.Admin;
        if (path.StartsWith("/api/quotas")) return Requirement.Authenticated;
        if (path.StartsWith("/api/solutions") || path.StartsWith("/api/judge")) return Requirement.Authenticated;

        if (path.StartsWith("/api/minecraft") || path.StartsWith("/api/integrations/minecraft")) return Requirement.Authenticated;

        return Requirement.Authenticated;
    }

    public static ClaimsPrincipal? ValidateUser(HttpContext context, IConfiguration config)
    {
        var token = ReadBearer(context) ?? ReadCookie(context, "tf_at") ?? ReadAccessTokenQuery(context);
        if (string.IsNullOrWhiteSpace(token)) return null;

        try
        {
            var key = Encoding.UTF8.GetBytes(config["Jwt:Key"] ?? config["Jwt:SigningKey"] ?? "dev_change_me_please_change_me_please_32_chars");
            return new JwtSecurityTokenHandler().ValidateToken(token, new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateIssuerSigningKey = true,
                ValidateLifetime = true,
                ValidIssuer = config["Jwt:Issuer"] ?? "TaskForge",
                ValidAudience = config["Jwt:Audience"] ?? "TaskForge",
                IssuerSigningKey = new SymmetricSecurityKey(key),
                ClockSkew = TimeSpan.FromSeconds(30)
            }, out _);
        }
        catch
        {
            return null;
        }
    }

    public static Guid? UserId(HttpContext context, IConfiguration config)
    {
        var principal = ValidateUser(context, config);
        var raw = principal?.FindFirstValue(ClaimTypes.NameIdentifier) ?? principal?.FindFirstValue("sub");
        return Guid.TryParse(raw, out var id) ? id : null;
    }

    public static bool HasAnyRole(ClaimsPrincipal principal, params string[] allowedRoles)
    {
        var allowed = allowedRoles.Select(x => x.Trim()).Where(x => x.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var roles = principal.Claims
            .Where(c => c.Type == ClaimTypes.Role || c.Type == "role" || c.Type == "roles" || c.Type == "primary_role")
            .SelectMany(c => c.Value.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return roles.Any(allowed.Contains);
    }

    private static bool IsInternalRequest(HttpContext context, IConfiguration config)
    {
        var expected = config["InternalApi:Key"]
            ?? config["TaskForgeInternalApi:ApiKey"]
            ?? config["TaskForge:InternalKey"]
            ?? Environment.GetEnvironmentVariable("TASKFORGE_INTERNAL_KEY")
            ?? Environment.GetEnvironmentVariable("TASKFORGE_AGENT_INTERNAL_KEY");
        if (string.IsNullOrWhiteSpace(expected)) return false;
        var provided = context.Request.Headers["X-Internal-Key"].ToString();
        return FixedEquals(provided, expected);
    }

    private static bool FixedEquals(string? a, string? b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
        var aa = Encoding.UTF8.GetBytes(a);
        var bb = Encoding.UTF8.GetBytes(b);
        return aa.Length == bb.Length && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(aa, bb);
    }

    private static string Normalize(PathString path)
    {
        var value = path.Value ?? string.Empty;
        if (value.Length > 1) value = value.TrimEnd('/');
        return value.ToLowerInvariant();
    }

    private static string? ReadBearer(HttpContext http)
    {
        var auth = http.Request.Headers.Authorization.ToString();
        return auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? auth[7..].Trim() : null;
    }

    private static string? ReadCookie(HttpContext http, string name) => http.Request.Cookies.TryGetValue(name, out var v) ? v : null;
    private static string? ReadAccessTokenQuery(HttpContext http) => http.Request.Query.TryGetValue("access_token", out var v) ? v.ToString() : null;

    private static async Task WriteProblem(HttpContext context, int statusCode, string message, string code)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsJsonAsync(new
        {
            status = statusCode,
            code,
            message,
            severity = statusCode >= 500 ? "error" : "warning"
        });
    }
}
