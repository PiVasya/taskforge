using System.Net.Http.Json;
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
    private static WebApplication MapAnalyticsEndpoints(WebApplication app)
    {
        app.MapGet("/api/admin/analytics/overview", async (ObservabilityDbContext db, IConfiguration cfg, IHttpClientFactory httpFactory, int days = 30, CancellationToken ct = default) =>
        {
            days = System.Math.Clamp(days, 1, 365);
            var now = DateTimeOffset.UtcNow;
            var since = now.AddDays(-days);
            var prevSince = since.AddDays(-days);
            var views = await db.PageViews.AsNoTracking().Where(x => x.CreatedAt >= since).ToListAsync(ct);
            var prevViews = await db.PageViews.AsNoTracking().Where(x => x.CreatedAt >= prevSince && x.CreatedAt < since).ToListAsync(ct);
            var users = await LoadUserSummariesAsync(views.Concat(prevViews).Select(x => x.UserId).Where(x => x.HasValue).Select(x => x!.Value), cfg, httpFactory, ct);

            var apiViews = views.Where(IsApiRequest).ToList();
            var prevApiViews = prevViews.Where(IsApiRequest).ToList();
            var errorViews = apiViews.Where(IsError).ToList();
            var prevErrorViews = prevApiViews.Where(IsError).ToList();
            var assignmentViews = apiViews.Where(IsAssignmentActivity).ToList();
            var successAssignmentViews = assignmentViews.Where(IsSuccess).ToList();
            var supportViews = apiViews.Where(x => Contains(x.Path, "/support")).ToList();
            var loginViews = views.Where(IsLogin).ToList();
            var activeUserCount = views.Where(x => x.UserId.HasValue).Select(x => x.UserId!.Value).Distinct().Count();
            var prevActiveUserCount = prevViews.Where(x => x.UserId.HasValue).Select(x => x.UserId!.Value).Distinct().Count();
            var avgLatency = AvgDuration(apiViews);
            var prevAvgLatency = AvgDuration(prevApiViews);
            var assignmentSuccessRate = Percent(successAssignmentViews.Count, System.Math.Max(1, assignmentViews.Count));
            var prevAssignmentViews = prevApiViews.Where(IsAssignmentActivity).ToList();
            var prevSuccessRate = Percent(prevAssignmentViews.Count(IsSuccess), System.Math.Max(1, prevAssignmentViews.Count));
            var supportStats = await LoadSupportAnalyticsAsync(cfg, httpFactory, since, now, days, supportViews, ct);

            var userRows = views.Where(x => x.UserId.HasValue).GroupBy(x => x.UserId!.Value).Select(g =>
            {
                var u = users.GetValueOrDefault(g.Key);
                var api = g.Where(IsApiRequest).ToList();
                var logins = g.Count(IsLogin);
                var requests = api.Count;
                var errors = api.Count(IsError);
                return new
                {
                    userId = g.Key,
                    login = u?.Login,
                    fullName = UserLabel(u),
                    displayName = UserLabel(u),
                    email = u?.Email ?? u?.MaskedEmail,
                    role = u?.Role ?? "User",
                    value = requests == 0 ? g.Count() : requests,
                    requests,
                    logins,
                    errors,
                    errorRate = Percent(errors, System.Math.Max(1, requests)),
                    avgLatencyMs = AvgDuration(api),
                    activeDays = g.Select(x => x.CreatedAt.UtcDateTime.Date).Distinct().Count(),
                    lastLoginAt = g.Where(IsLogin).Select(x => x.CreatedAt).DefaultIfEmpty(g.Max(x => x.CreatedAt)).Max(),
                    lastSeenAt = g.Max(x => x.CreatedAt)
                };
            }).OrderByDescending(x => x.requests).Take(50).ToList();

            var topEndpoints = apiViews.GroupBy(x => NormalizeEndpoint(x.Path)).Select(g =>
            {
                var requests = g.Count();
                var errors = g.Count(IsError);
                var avg = AvgDuration(g);
                return new
                {
                    label = g.Key,
                    value = requests,
                    requests,
                    count = requests,
                    errors,
                    avgLatencyMs = avg,
                    p95LatencyMs = PercentileDuration(g, 0.95),
                    errorRate = Percent(errors, requests),
                    lastErrorAt = g.Where(IsError).Select(x => x.CreatedAt).OrderByDescending(x => x).FirstOrDefault()
                };
            }).OrderByDescending(x => x.value).Take(25).ToList();

            var slowEndpoints = topEndpoints
                .Where(x => x.requests >= 2 || x.avgLatencyMs > 0)
                .OrderByDescending(x => x.avgLatencyMs)
                .Take(10)
                .Select(x => new { x.label, value = x.avgLatencyMs, x.avgLatencyMs, x.p95LatencyMs, x.requests, x.errors, x.errorRate })
                .ToList();

            var errorEndpoints = errorViews.GroupBy(x => NormalizeEndpoint(x.Path)).Select(g => new
            {
                label = g.Key,
                value = g.Count(),
                requests = g.Count(),
                errors = g.Count(),
                statusCodes = g.GroupBy(x => x.StatusCode?.ToString() ?? "unknown").Select(x => new { label = x.Key, value = x.Count() }).OrderByDescending(x => x.value).ToList(),
                lastErrorAt = g.Max(x => x.CreatedAt),
                sample = g.OrderByDescending(x => x.CreatedAt).Select(x => x.ErrorMessage ?? x.ErrorCode ?? $"HTTP {x.StatusCode}").FirstOrDefault()
            }).OrderByDescending(x => x.value).Take(20).ToList();

            var assignmentRows = assignmentViews.GroupBy(x => AssignmentLabel(x.Path)).Select(g =>
            {
                var attempts = g.Count();
                var passed = g.Count(IsSuccess);
                var label = g.Key;
                return new
                {
                    assignmentId = (Guid?)null,
                    title = string.IsNullOrWhiteSpace(label) ? "Задание без названия" : label,
                    type = AssignmentTypeFromPath(g.First().Path),
                    difficulty = 0,
                    rating = 0,
                    attempts,
                    passed,
                    successRate = Percent(passed, attempts),
                    value = attempts
                };
            }).OrderByDescending(x => x.attempts).Take(20).ToList();

            var hardAssignments = assignmentRows.Where(x => x.attempts >= 2).OrderBy(x => x.successRate).ThenByDescending(x => x.attempts).Take(20).ToList();
            var ipPrefixes = apiViews.Where(x => !string.IsNullOrWhiteSpace(x.ClientIpPrefix)).GroupBy(x => x.ClientIpPrefix!).Select(g => new
            {
                label = g.Key,
                value = g.Count(),
                requests = g.Count(),
                errors = g.Count(IsError),
                countries = g.Where(x => !string.IsNullOrWhiteSpace(x.ClientCountry)).GroupBy(x => x.ClientCountry!).Select(x => new { label = x.Key, value = x.Count() }).OrderByDescending(x => x.value).Take(3).ToList()
            }).OrderByDescending(x => x.value).Take(15).ToList();

            return Microsoft.AspNetCore.Http.Results.Ok(new
            {
                periodDays = days,
                privacy = new
                {
                    ipMode = "anonymized",
                    rawIpStored = false,
                    ipPrefix = "IPv4 /24, IPv6 /48",
                    note = "Сырые IP не сохраняются: хранится только хэш и укрупнённая подсеть."
                },
                users = new
                {
                    totals = new { activeUsers = activeUserCount, totalUsers = users.Count, newUsers = 0, logins = loginViews.Count },
                    uniqueUsersByDay = DayPoints(views, days, g => g.Where(x => x.UserId.HasValue).Select(x => x.UserId!.Value).Distinct().Count()),
                    loginsByDay = DayPoints(loginViews, days, g => g.Count()),
                    loginsByHour = HourPoints(loginViews),
                    roleDistribution = userRows.GroupBy(x => x.role).Select(g => new { label = g.Key, value = g.Count() }).ToList(),
                    topUsers = userRows.OrderByDescending(x => x.value).Take(25).ToList()
                },
                api = new
                {
                    totals = new { totalRequests = apiViews.Count, errors4xx = apiViews.Count(x => x.StatusCode is >= 400 and < 500), errors5xx = apiViews.Count(x => x.StatusCode is >= 500), avgLatencyMs = avgLatency, p95LatencyMs = PercentileDuration(apiViews, 0.95), p99LatencyMs = PercentileDuration(apiViews, 0.99), errorRate = Percent(errorViews.Count, System.Math.Max(1, apiViews.Count)) },
                    requestsByDay = DayPoints(apiViews, days, g => g.Count()),
                    errorsByDay = DayPoints(errorViews, days, g => g.Count()),
                    latencyByDay = DayPoints(apiViews, days, g => AvgDuration(g)),
                    clientTypes = apiViews.GroupBy(ClientType).Select(g => new { label = g.Key, value = g.Count() }).OrderByDescending(x => x.value).ToList(),
                    statusCodes = apiViews.GroupBy(x => x.StatusCode?.ToString() ?? "unknown").Select(g => new { label = g.Key, value = g.Count() }).OrderByDescending(x => x.value).ToList(),
                    ipPrefixes,
                    topEndpoints,
                    errorEndpoints,
                    topUsers = userRows.OrderByDescending(x => x.requests).Take(25).ToList()
                },
                assignments = new
                {
                    totals = new { totalAttempts = assignmentViews.Count, successRate = assignmentSuccessRate, codeAttempts = assignmentViews.Count(x => Contains(x.Path, "/solutions") || Contains(x.Path, "/submissions") || Contains(x.Path, "/judge")), testAttempts = assignmentViews.Count(x => Contains(x.Path, "test")), imageAttempts = assignmentViews.Count(x => Contains(x.Path, "image")), mathAttempts = assignmentViews.Count(x => Contains(x.Path, "math")), avgTestScore = assignmentSuccessRate },
                    attemptsByDay = DayPoints(assignmentViews, days, g => g.Count()),
                    successByDay = DayPoints(successAssignmentViews, days, g => g.Count()),
                    types = assignmentViews.GroupBy(x => AssignmentTypeFromPath(x.Path)).Select(g => new { label = g.Key, value = g.Count() }).OrderByDescending(x => x.value).ToList(),
                    languages = apiViews.Select(x => LanguageFromPath(x.Path)).Where(x => x != null).GroupBy(x => x!).Select(g => new { label = g.Key, value = g.Count() }).OrderByDescending(x => x.value).Take(10).ToList(),
                    topAssignments = assignmentRows,
                    hardAssignments
                },
                support = supportStats,
                executive = new
                {
                    comparisons = new object[]
                    {
                        Comparison("Активные пользователи", activeUserCount, prevActiveUserCount),
                        Comparison("Запросы API", apiViews.Count, prevApiViews.Count),
                        Comparison("Ошибки API", errorViews.Count, prevErrorViews.Count),
                        Comparison("Средняя задержка", avgLatency, prevAvgLatency, "мс"),
                        Comparison("Успешность заданий", assignmentSuccessRate, prevSuccessRate, "%", true)
                    },
                    alerts = BuildAlerts(errorViews.Count, apiViews.Count, avgLatency, assignmentSuccessRate),
                    noisyUsers = userRows.OrderByDescending(x => x.requests).Take(5),
                    slowEndpoints,
                    failingAssignments = hardAssignments.Take(5)
                },
                alerts = BuildAlerts(errorViews.Count, apiViews.Count, avgLatency, assignmentSuccessRate),
                comparison = new { previousRequests = prevApiViews.Count, requestDelta = apiViews.Count - prevApiViews.Count }
            });
        });

        app.MapGet("/api/admin/analytics/users/search", async (ObservabilityDbContext db, IConfiguration cfg, IHttpClientFactory httpFactory, string? q, int take = 20, int? limit = null, CancellationToken ct = default) =>
        {
            var query = NormalizeSearch(q);
            var size = System.Math.Clamp(limit ?? take, 1, 100);
            var rows = await db.PageViews.AsNoTracking().Where(x => x.UserId.HasValue).GroupBy(x => x.UserId).Select(g => new { userId = g.Key!.Value, lastSeenAt = g.Max(x => x.CreatedAt), requests = g.Count(x => x.Action == "api-request" || x.Action == "api-error"), lastLoginAt = g.Where(x => x.Action == "login" || x.Path.Contains("/login")).Max(x => (DateTimeOffset?)x.CreatedAt) }).OrderByDescending(x => x.lastSeenAt).Take(500).ToListAsync(ct);
            var users = await LoadUserSummariesAsync(rows.Select(x => x.userId), cfg, httpFactory, ct);
            var result = rows.Select(x =>
            {
                var u = users.GetValueOrDefault(x.userId);
                return new { userId = x.userId, login = u?.Login, fullName = UserLabel(u), displayName = UserLabel(u), email = u?.Email ?? u?.MaskedEmail, role = u?.Role ?? "User", value = x.requests, requests = x.requests, lastSeenAt = x.lastSeenAt, lastLoginAt = x.lastLoginAt ?? x.lastSeenAt };
            }).Where(x => string.IsNullOrWhiteSpace(query) || NormalizeSearch($"{x.fullName} {x.email} {x.login} {x.role}").Contains(query)).Take(size).ToList();
            return Microsoft.AspNetCore.Http.Results.Ok(result);
        });

        app.MapGet("/api/admin/analytics/users/{userId:guid}", async (Guid userId, ObservabilityDbContext db, IConfiguration cfg, IHttpClientFactory httpFactory, int days = 30, CancellationToken ct = default) =>
        {
            days = System.Math.Clamp(days, 1, 365);
            var since = DateTimeOffset.UtcNow.AddDays(-days);
            var rows = await db.PageViews.AsNoTracking().Where(x => x.UserId == userId && x.CreatedAt >= since).OrderByDescending(x => x.CreatedAt).Take(1000).ToListAsync(ct);
            var apiRows = rows.Where(IsApiRequest).ToList();
            var users = await LoadUserSummariesAsync(new[] { userId }, cfg, httpFactory, ct);
            var u = users.GetValueOrDefault(userId);
            return Microsoft.AspNetCore.Http.Results.Ok(new
            {
                userId,
                periodDays = days,
                profile = new { userId, login = u?.Login, fullName = UserLabel(u), displayName = UserLabel(u), email = u?.Email ?? u?.MaskedEmail, role = u?.Role ?? "User", lastLoginAt = rows.Where(IsLogin).Select(x => x.CreatedAt).DefaultIfEmpty(rows.FirstOrDefault()?.CreatedAt ?? DateTimeOffset.MinValue).Max() },
                activity = new { totalLogins = rows.Count(IsLogin), totalRequests = apiRows.Count, errorRequests = apiRows.Count(IsError), requests = apiRows.Count, errors = apiRows.Count(IsError), avgLatencyMs = AvgDuration(apiRows), codeSubmits = apiRows.Count(x => Contains(x.Path, "/solutions") || Contains(x.Path, "/judge")), imageSubmits = apiRows.Count(x => Contains(x.Path, "image")), testAttempts = apiRows.Count(x => Contains(x.Path, "test")), mathAttempts = apiRows.Count(x => Contains(x.Path, "math")), submissions = apiRows.Count(IsAssignmentActivity), ticketsCreated = apiRows.Count(x => Contains(x.Path, "/support")) },
                charts = new { loginsByDay = DayPoints(rows.Where(IsLogin), days, g => g.Count()), requestsByDay = DayPoints(apiRows, days, g => g.Count()), requestsByHour = HourPoints(apiRows) },
                topPaths = apiRows.GroupBy(x => NormalizeEndpoint(x.Path)).Select(g => new { label = g.Key, value = g.Count(), avgLatencyMs = AvgDuration(g), errors = g.Count(IsError), errorRate = Percent(g.Count(IsError), g.Count()) }).OrderByDescending(x => x.value).Take(20).ToList(),
                submissions = apiRows.Where(IsAssignmentActivity).Take(100).ToList(),
                logins = rows.Where(IsLogin).Take(100).ToList(),
                recentActivity = rows.Take(100).ToList()
            });
        });

        return app;
    }

    private static async Task<object> LoadSupportAnalyticsAsync(IConfiguration cfg, IHttpClientFactory httpFactory, DateTimeOffset fromUtc, DateTimeOffset toUtc, int days, List<PageView> fallbackViews, CancellationToken ct)
    {
        try
        {
            var client = httpFactory.CreateClient();
            var url = $"{ServiceUrl(cfg, "SupportApi", "http://support-api:8080")}/api/internal/support/analytics/summary?fromUtc={Uri.EscapeDataString(fromUtc.ToString("O"))}&toUtc={Uri.EscapeDataString(toUtc.ToString("O"))}&days={days}";
            using var msg = new HttpRequestMessage(HttpMethod.Get, url);
            AddInternalKey(msg, cfg);
            using var resp = await client.SendAsync(msg, ct);
            if (resp.IsSuccessStatusCode)
            {
                var json = await resp.Content.ReadFromJsonAsync<JsonElement>(JsonOptions(), ct);
                return json;
            }
        }
        catch
        {
        }

        return new
        {
            totals = new { totalTickets = fallbackViews.Count(x => string.Equals(x.Method, "POST", StringComparison.OrdinalIgnoreCase)), openTickets = 0, avgFirstResponseMinutes = 0, avgCloseMinutes = 0 },
            ticketsByDay = DayPoints(fallbackViews.Where(x => string.Equals(x.Method, "POST", StringComparison.OrdinalIgnoreCase)), days, g => g.Count()),
            closedByDay = DayPoints(fallbackViews.Where(x => Contains(x.Path, "closed") || Contains(x.Path, "resolved")), days, g => g.Count()),
            ticketTypes = fallbackViews.GroupBy(x => Contains(x.Path, "admin") ? "Админка" : "Пользователь").Select(g => new { label = g.Key, value = g.Count() }).ToList(),
            topAdmins = Array.Empty<object>()
        };
    }
}
