using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TaskForge.Observability.Api.Data;
using TaskForge.Observability.Api.Domain;

using TaskForge.Observability.Api.Contracts;
using static TaskForge.Observability.Api.Services.Common.ObservabilityApiCommonService;
using static TaskForge.Observability.Api.Services.Mapping.ObservabilityApiMappingService;
using static TaskForge.Observability.Api.Services.Serialization.ObservabilityApiSerializationService;

namespace TaskForge.Observability.Api.Endpoints;

internal static partial class ObservabilityApiEndpoints
{
    private static WebApplication MapActivityEndpoints(WebApplication app)
    {
        app.MapPost("/api/activity/page-view", async (PageViewRequest req, HttpContext http, IConfiguration cfg, ObservabilityDbContext db, CancellationToken ct) =>
        {
            var ipMetadata = BuildIpMetadata(http, cfg);
            var view = new PageView
            {
                UserId = TaskForgeRequestSecurity.UserId(http, cfg),
                Path = NormalizeClientPath(req.Path ?? req.Url ?? http.Request.Headers.Referer.ToString() ?? "/"),
                Method = NormalizeMethod(req.Method),
                Action = NormalizeAction(req.Action),
                StatusCode = req.StatusCode,
                DurationMs = req.DurationMs,
                UserAgent = Truncate(http.Request.Headers.UserAgent.ToString(), 800),
                Source = Truncate(req.Source, 80),
                TraceId = Truncate(req.TraceId, 160),
                ErrorCode = Truncate(req.ErrorCode, 120),
                ErrorMessage = Truncate(req.ErrorMessage, 1000),
                ClientIpHash = ipMetadata.Hash,
                ClientIpPrefix = ipMetadata.Prefix,
                ClientCountry = Truncate(ReadHeader(http, "CF-IPCountry"), 8),
            };
            db.PageViews.Add(view);
            await db.SaveChangesAsync(ct);
            return Microsoft.AspNetCore.Http.Results.Ok(new { saved = true, view.Id });
        });

        app.MapGet("/api/admin/activity", async (
            ObservabilityDbContext db,
            IConfiguration cfg,
            IHttpClientFactory httpFactory,
            int days = 7,
            int page = 1,
            int pageSize = 50,
            string? q = null,
            string? query = null,
            string? category = null,
            string? actionType = null,
            string? source = null,
            Guid? userId = null,
            int? take = null,
            CancellationToken ct = default) =>
        {
            days = System.Math.Clamp(days, 1, 365);
            page = System.Math.Max(1, page);
            pageSize = System.Math.Clamp(take ?? pageSize, 10, 1000);

            var nowUtc = DateTimeOffset.UtcNow;
            var fromUtc = nowUtc.AddDays(-days);
            var search = NormalizeSearch(q ?? query);

            var dbQuery = db.PageViews.AsNoTracking()
                .Where(x => x.CreatedAt >= fromUtc && x.CreatedAt <= nowUtc);

            if (userId.HasValue) dbQuery = dbQuery.Where(x => x.UserId == userId);

            var rows = await dbQuery.OrderByDescending(x => x.CreatedAt).ToListAsync(ct);
            var users = await LoadUserSummariesAsync(rows.Select(x => x.UserId).Where(x => x.HasValue).Select(x => x!.Value), cfg, httpFactory, ct);

            var items = rows.Select(x => ToActivityItem(x, x.UserId.HasValue ? users.GetValueOrDefault(x.UserId.Value) : null)).ToList();

            if (!string.IsNullOrWhiteSpace(category)) items = items.Where(x => string.Equals(x.Category, category, StringComparison.OrdinalIgnoreCase)).ToList();
            if (!string.IsNullOrWhiteSpace(actionType)) items = items.Where(x => string.Equals(x.ActionType, actionType, StringComparison.OrdinalIgnoreCase)).ToList();
            if (!string.IsNullOrWhiteSpace(source)) items = items.Where(x => string.Equals(x.Source, source, StringComparison.OrdinalIgnoreCase)).ToList();
            if (!string.IsNullOrWhiteSpace(search))
            {
                items = items.Where(x => NormalizeSearch($"{x.User?.FullName} {x.User?.Email} {x.Category} {x.ActionType} {x.Source} {x.Method} {x.Path} {x.Target} {x.Description} {x.StatusCode}").Contains(search)).ToList();
            }

            var total = items.Count;
            var pageItems = items.Skip((page - 1) * pageSize).Take(pageSize).ToList();

            var topCategories = items.GroupBy(x => x.Category)
                .Select(g => new { label = g.Key, value = g.Count() })
                .OrderByDescending(x => x.value)
                .Take(10)
                .ToList();

            var topActions = items.GroupBy(x => x.ActionType)
                .Select(g => new { label = g.Key, value = g.Count() })
                .OrderByDescending(x => x.value)
                .Take(10)
                .ToList();

            var topUsers = items.Where(x => x.User != null)
                .GroupBy(x => new { x.User!.Id, x.User.FullName, x.User.Email, x.User.Role })
                .Select(g => new { userId = g.Key.Id, fullName = g.Key.FullName, displayName = g.Key.FullName, email = g.Key.Email, role = g.Key.Role, value = g.Count() })
                .OrderByDescending(x => x.value)
                .Take(10)
                .ToList();

            return Microsoft.AspNetCore.Http.Results.Ok(new
            {
                period = new { days, fromUtc, toUtc = nowUtc },
                paging = new { page, pageSize, total },
                totals = new
                {
                    totalActions = total,
                    uniqueUsers = items.Where(x => x.UserId.HasValue).Select(x => x.UserId!.Value).Distinct().Count(),
                    errors = items.Count(x => x.StatusCode.HasValue && x.StatusCode.Value >= 400),
                    navigations = items.Count(x => string.Equals(x.Category, "navigation", StringComparison.OrdinalIgnoreCase)),
                },
                filters = new { q, query, category, actionType, source, userId },
                topCategories,
                topActions,
                topUsers,
                items = pageItems,
            });
        });

        return app;
    }

    private static string NormalizeClientPath(string value)
    {
        var raw = string.IsNullOrWhiteSpace(value) ? "/" : value.Trim();
        if (Uri.TryCreate(raw, UriKind.Absolute, out var uri)) raw = string.IsNullOrWhiteSpace(uri.Query) ? uri.AbsolutePath : uri.PathAndQuery;
        if (!raw.StartsWith('/')) raw = "/" + raw;
        return Truncate(raw, 2048) ?? "/";
    }

    private static string NormalizeMethod(string? method)
    {
        var normalized = string.IsNullOrWhiteSpace(method) ? "GET" : method.Trim().ToUpperInvariant();
        return Truncate(normalized, 20) ?? "GET";
    }

    private static string NormalizeAction(string? action)
    {
        var normalized = string.IsNullOrWhiteSpace(action) ? "page-view" : action.Trim();
        return Truncate(normalized, 120) ?? "page-view";
    }

    private static string? Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }

    private static string? ReadHeader(HttpContext http, string name)
    {
        var value = http.Request.Headers[name].FirstOrDefault();
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static (string? Hash, string? Prefix) BuildIpMetadata(HttpContext http, IConfiguration cfg)
    {
        if (!cfg.GetValue("Analytics:CollectIpMetadata", true)) return (null, null);
        var raw = ReadClientIp(http);
        if (!IPAddress.TryParse(raw, out var ip)) return (null, null);
        var prefix = AnonymizeIp(ip);
        var salt = FirstNonEmpty(
            cfg["Analytics:IpHashSalt"],
            cfg["InternalApi:Key"],
            cfg["Jwt:SigningKey"],
            cfg["Jwt:Key"],
            "taskforge-observability-dev-salt");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{salt}:{ip}"))).ToLowerInvariant();
        return (hash, prefix);
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))!.Trim();
    }

    private static string? ReadClientIp(HttpContext http)
    {
        var candidates = new[]
        {
            ReadHeader(http, "CF-Connecting-IP"),
            ReadHeader(http, "X-Real-IP"),
            ReadHeader(http, "X-Forwarded-For")?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(),
            http.Connection.RemoteIpAddress?.ToString(),
        };
        return candidates.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
    }

    private static string? AnonymizeIp(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        var bytes = ip.GetAddressBytes();
        if (bytes.Length == 4) return $"{bytes[0]}.{bytes[1]}.{bytes[2]}.0/24";
        if (bytes.Length == 16)
        {
            var groups = Enumerable.Range(0, 3)
                .Select(i => ((bytes[i * 2] << 8) | bytes[i * 2 + 1]).ToString("x"));
            return string.Join(':', groups) + "::/48";
        }
        return null;
    }
}
