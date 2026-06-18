using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.IdentityModel.Tokens;
using TaskForge.Identity.Api.Data;
using TaskForge.Identity.Api.Domain;

using TaskForge.Identity.Api.Contracts;
using static TaskForge.Identity.Api.Services.Access.IdentityApiAccessService;
using static TaskForge.Identity.Api.Services.Image.IdentityApiImageService;
using static TaskForge.Identity.Api.Services.Mapping.IdentityApiMappingService;
using static TaskForge.Identity.Api.Services.Results.IdentityApiResultsService;
using static TaskForge.Identity.Api.Services.Serialization.IdentityApiSerializationService;

namespace TaskForge.Identity.Api.Services.Common;

internal static class IdentityApiCommonService
{
    internal static IResult? CheckAuthRateLimit(HttpContext http, string bucket, string? identity = null)
    {
        var ip = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var normalizedIdentity = string.IsNullOrWhiteSpace(identity) ? "none" : (NormalizeOptionalEmail(identity) ?? NormalizeLogin(identity));
        var key = $"{bucket}:{ip}:{normalizedIdentity}";
        if (TaskForgeAuthRateLimiters.Allow(bucket, key)) return null;

        return Microsoft.AspNetCore.Http.Results.Json(new
        {
            message = "Слишком много попыток. Подождите немного и попробуйте снова.",
            code = "RATE_LIMITED"
        }, statusCode: StatusCodes.Status429TooManyRequests);
    }

    internal static IQueryable<IdentityUser> FilterUsers(IQueryable<IdentityUser> query, string? text, string? role, bool linkedOnly)
    {
        if (!string.IsNullOrWhiteSpace(text))
        {
            var q = text.Trim().ToLowerInvariant();
            query = query.Where(x => ((x.Login != null && x.Login.ToLower().Contains(q)) || (x.Email != null && x.Email.ToLower().Contains(q)) || x.FirstName.ToLower().Contains(q)) || x.LastName.ToLower().Contains(q));
        }
        if (!string.IsNullOrWhiteSpace(role)) query = query.Where(x => x.Role == role.Trim());
        if (linkedOnly) query = query.Where(x => false);
        return query;
    }

    internal static async Task<List<IdentityUser>> SearchUsersAsync(IdentityDbContext db, string? text, string? role, bool linkedOnly, string? sortBy, string? sortDir, int take)
    {
        var query = db.Users.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(role)) query = query.Where(x => x.Role == role.Trim());
        if (linkedOnly) return new List<IdentityUser>();

        var q = NormalizeSearch(text);
        if (string.IsNullOrWhiteSpace(q))
        {
            return await SortUsers(query, sortBy, sortDir).Take(System.Math.Clamp(take, 1, 500)).ToListAsync();
        }

        var rows = await query.Take(5000).ToListAsync();
        var maxDistance = System.Math.Max(1, System.Math.Min(4, q.Length / 3));
        return rows
            .Select(u => new { User = u, Score = UserSearchScore(u, q) })
            .Where(x => x.Score <= maxDistance || UserSearchHaystack(x.User).Contains(q, StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.Score)
            .ThenBy(x => x.User.Login ?? x.User.Email ?? x.User.Id.ToString())
            .Take(System.Math.Clamp(take, 1, 500))
            .Select(x => x.User)
            .ToList();
    }

    internal static async Task<int> CountUsersAsync(IdentityDbContext db, string? text, string? role, bool linkedOnly)
    {
        if (linkedOnly) return 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            var query = db.Users.AsNoTracking();
            if (!string.IsNullOrWhiteSpace(role)) query = query.Where(x => x.Role == role.Trim());
            return await query.CountAsync();
        }
        return (await SearchUsersAsync(db, text, role, linkedOnly, "login", "asc", 5000)).Count;
    }

    internal static string UserSearchHaystack(IdentityUser user)
        => NormalizeSearch($"{user.Login} {user.Email} {user.FirstName} {user.LastName} {DisplayName(user)} {user.Id}");

    internal static int Levenshtein(string a, string b)
    {
        if (a == b) return 0;
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = System.Math.Min(System.Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }

    internal static async Task<ActivitySummaryDto> FetchUserActivitySummaryAsync(Guid userId, IConfiguration cfg, IHttpClientFactory httpFactory, CancellationToken ct)
    {
        var solutions = await FetchActivitySummaryFromAsync(ServiceUrl(cfg, "SolutionsApi", "http://solutions-api:8080"), userId, cfg, httpFactory, ct);
        var tasks = await FetchActivitySummaryFromAsync(ServiceUrl(cfg, "TasksApi", "http://tasks-api:8080"), userId, cfg, httpFactory, ct);
        return new ActivitySummaryDto(
            solutions.SolvedAssignments + tasks.SolvedAssignments,
            solutions.TotalAttempts + tasks.TotalAttempts,
            solutions.CodeSolutions,
            solutions.ImageSolutions,
            tasks.TestAttempts,
            tasks.MathAttempts,
            solutions.Score + tasks.Score);
    }

    internal static async Task<ActivitySummaryDto> FetchActivitySummaryFromAsync(string baseUrl, Guid userId, IConfiguration cfg, IHttpClientFactory httpFactory, CancellationToken ct)
    {
        try
        {
            var client = httpFactory.CreateClient();
            using var msg = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl.TrimEnd('/')}/api/internal/users/{userId}/activity-summary");
            AddInternalKey(msg, cfg);
            using var resp = await client.SendAsync(msg, ct);
            if (!resp.IsSuccessStatusCode) return ActivitySummaryDto.Empty;
            return await resp.Content.ReadFromJsonAsync<ActivitySummaryDto>(JsonOptions(), ct) ?? ActivitySummaryDto.Empty;
        }
        catch
        {
            return ActivitySummaryDto.Empty;
        }
    }

    internal static string ServiceUrl(IConfiguration cfg, string name, string fallback)
        => (cfg[$"Services:{name}"] ?? cfg[$"ServiceUrls:{name}"] ?? fallback).TrimEnd('/');

    internal static void AddInternalKey(HttpRequestMessage msg, IConfiguration cfg)
    {
        var key = cfg["InternalApi:Key"] ?? cfg["TaskForgeInternalApi:ApiKey"] ?? cfg["TaskForge:InternalKey"] ?? Environment.GetEnvironmentVariable("TASKFORGE_INTERNAL_KEY");
        if (!string.IsNullOrWhiteSpace(key)) msg.Headers.TryAddWithoutValidation("X-Internal-Key", key);
    }

    internal static IQueryable<IdentityUser> SortUsers(IQueryable<IdentityUser> query, string? sortBy, string? sortDir)
    {
        var desc = !string.Equals(sortDir, "asc", StringComparison.OrdinalIgnoreCase);
        return (sortBy ?? "createdAt") switch
        {
            "login" => desc ? query.OrderByDescending(x => x.Login) : query.OrderBy(x => x.Login),
            "email" => desc ? query.OrderByDescending(x => x.Email) : query.OrderBy(x => x.Email),
            "role" => desc ? query.OrderByDescending(x => x.Role) : query.OrderBy(x => x.Role),
            "fullName" => desc ? query.OrderByDescending(x => x.FirstName).ThenByDescending(x => x.LastName) : query.OrderBy(x => x.FirstName).ThenBy(x => x.LastName),
            "lastLoginAt" => desc ? query.OrderByDescending(x => x.LastLoginAt) : query.OrderBy(x => x.LastLoginAt),
            _ => desc ? query.OrderByDescending(x => x.CreatedAt) : query.OrderBy(x => x.CreatedAt)
        };
    }

    internal static string UserLoginOrFallback(IdentityUser user)
        => !string.IsNullOrWhiteSpace(user.Login) ? user.Login! : user.Email ?? user.Id.ToString();

    internal static async Task BackfillUserLoginsAsync(IdentityDbContext db, ILogger logger)
    {
        var users = await db.Users.OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).ToListAsync();
        if (users.Count == 0) return;

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var user in users)
        {
            var normalized = NormalizeLogin(user.Login);
            if (!string.IsNullOrWhiteSpace(normalized) && IsValidLogin(normalized, out _))
            {
                user.Login = normalized;
                used.Add(normalized);
            }
            else if (!string.IsNullOrWhiteSpace(user.Login))
            {
                user.Login = null;
            }
        }

        var changed = 0;
        foreach (var user in users.Where(x => string.IsNullOrWhiteSpace(x.Login)))
        {
            var login = BuildBackfilledLogin(user, used);
            user.Login = login;
            used.Add(login);
            changed++;
        }

        if (changed > 0)
        {
            await db.SaveChangesAsync();
            logger.LogInformation("Backfilled {Count} missing user logins.", changed);
        }
    }

    internal static IResult Unauthorized(string message, string code = "UNAUTHORIZED") => Microsoft.AspNetCore.Http.Results.Json(new { message, code, severity = "warning" }, statusCode: StatusCodes.Status401Unauthorized);

    internal static bool IsUnsafeProductionSecret(string? value, int minLength)
    {
        var v = (value ?? string.Empty).Trim();
        if (v.Length < minLength) return true;
        if (v.Contains("CHANGE_ME", StringComparison.OrdinalIgnoreCase)) return true;
        if (v.Contains("dev_change_me", StringComparison.OrdinalIgnoreCase)) return true;
        if (v.Contains("password", StringComparison.OrdinalIgnoreCase)) return true;
        if (v.Distinct().Count() < 8) return true;
        return false;
    }

    internal static bool IsValidLogin(string? login, out string message)
    {
        var value = (login ?? string.Empty).Trim();
        if (value.Length < 3)
        {
            message = "Логин должен быть не короче 3 символов.";
            return false;
        }
        if (value.Length > 64)
        {
            message = "Логин должен быть не длиннее 64 символов.";
            return false;
        }
        if (value.Any(ch => !((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9') || ch == '_' || ch == '.' || ch == '-')))
        {
            message = "Логин может содержать только латинские буквы, цифры, точку, дефис и подчёркивание.";
            return false;
        }
        message = string.Empty;
        return true;
    }

    internal static string NewSalt() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    internal static string PublicDisplayName(IdentityUser user) => DisplayName(user);

    internal static string DisplayName(IdentityUser user)
    {
        var full = string.Join(' ', new[] { user.FirstName, user.LastName }.Where(x => !string.IsNullOrWhiteSpace(x))).Trim();
        if (!string.IsNullOrWhiteSpace(full)) return full;
        if (!string.IsNullOrWhiteSpace(user.Login)) return user.Login!;
        return "Пользователь";
    }

    internal static void SetAuthCookies(HttpContext http, string access, string refresh, TimeSpan accessLifetime, TimeSpan refreshLifetime)
    {
        var secure = string.Equals(http.Request.Headers["X-Forwarded-Proto"].ToString(), "https", StringComparison.OrdinalIgnoreCase) || http.Request.IsHttps;
        http.Response.Cookies.Append("tf_at", access, new CookieOptions { HttpOnly = true, Secure = secure, SameSite = SameSiteMode.Lax, Expires = DateTimeOffset.UtcNow.Add(accessLifetime), Path = "/" });
        http.Response.Cookies.Append("tf_rt", refresh, new CookieOptions { HttpOnly = true, Secure = secure, SameSite = SameSiteMode.Lax, Expires = DateTimeOffset.UtcNow.Add(refreshLifetime), Path = "/" });
    }

    internal static void ClearAuthCookies(HttpContext http)
    {
        http.Response.Cookies.Delete("tf_at", new CookieOptions { Path = "/" });
        http.Response.Cookies.Delete("tf_rt", new CookieOptions { Path = "/" });
    }

}
