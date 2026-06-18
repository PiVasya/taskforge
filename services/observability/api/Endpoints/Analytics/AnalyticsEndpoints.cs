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

            var errorViews = views.Where(IsError).ToList();
            var assignmentViews = views.Where(IsAssignmentActivity).ToList();
            var successAssignmentViews = assignmentViews.Where(IsSuccess).ToList();
            var supportViews = views.Where(x => Contains(x.Path, "/support")).ToList();
            var loginViews = views.Where(IsLogin).ToList();
            var activeUserCount = views.Where(x => x.UserId.HasValue).Select(x => x.UserId!.Value).Distinct().Count();
            var prevActiveUserCount = prevViews.Where(x => x.UserId.HasValue).Select(x => x.UserId!.Value).Distinct().Count();
            var avgLatency = AvgDuration(views);
            var prevAvgLatency = AvgDuration(prevViews);
            var assignmentSuccessRate = Percent(successAssignmentViews.Count, System.Math.Max(1, assignmentViews.Count));
            var prevAssignmentViews = prevViews.Where(IsAssignmentActivity).ToList();
            var prevSuccessRate = Percent(prevAssignmentViews.Count(IsSuccess), System.Math.Max(1, prevAssignmentViews.Count));

            var userRows = views.Where(x => x.UserId.HasValue).GroupBy(x => x.UserId!.Value).Select(g =>
            {
                var u = users.GetValueOrDefault(g.Key);
                return new
                {
                    userId = g.Key,
                    login = u?.Login,
                    fullName = UserLabel(u),
                    displayName = UserLabel(u),
                    email = u?.Email ?? u?.MaskedEmail,
                    role = u?.Role ?? "User",
                    value = g.Count(IsLogin) == 0 ? g.Count() : g.Count(IsLogin),
                    requests = g.Count(),
                    errors = g.Count(IsError),
                    avgLatencyMs = AvgDuration(g),
                    activeDays = g.Select(x => x.CreatedAt.UtcDateTime.Date).Distinct().Count(),
                    lastLoginAt = g.Where(IsLogin).Select(x => x.CreatedAt).DefaultIfEmpty(g.Max(x => x.CreatedAt)).Max(),
                    lastSeenAt = g.Max(x => x.CreatedAt)
                };
            }).OrderByDescending(x => x.requests).Take(25).ToList();

            var topEndpoints = views.GroupBy(x => NormalizeEndpoint(x.Path)).Select(g => new
            {
                label = g.Key,
                value = g.Count(),
                requests = g.Count(),
                count = g.Count(),
                avgLatencyMs = AvgDuration(g),
                errorRate = Percent(g.Count(IsError), g.Count())
            }).OrderByDescending(x => x.value).Take(25).ToList();

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

            return Microsoft.AspNetCore.Http.Results.Ok(new
            {
                periodDays = days,
                users = new
                {
                    totals = new { activeUsers = activeUserCount, totalUsers = users.Count, newUsers = 0, logins = loginViews.Count },
                    uniqueUsersByDay = DayPoints(views, days, g => g.Where(x => x.UserId.HasValue).Select(x => x.UserId!.Value).Distinct().Count()),
                    loginsByDay = DayPoints(loginViews, days, g => g.Count()),
                    loginsByHour = HourPoints(loginViews),
                    roleDistribution = userRows.GroupBy(x => x.role).Select(g => new { label = g.Key, value = g.Count() }).ToList(),
                    topUsers = userRows.OrderByDescending(x => x.value).ToList()
                },
                api = new
                {
                    totals = new { totalRequests = views.Count, errors4xx = views.Count(x => x.StatusCode is >= 400 and < 500), errors5xx = views.Count(x => x.StatusCode is >= 500), avgLatencyMs = avgLatency, p95LatencyMs = PercentileDuration(views, 0.95), p99LatencyMs = PercentileDuration(views, 0.99) },
                    requestsByDay = DayPoints(views, days, g => g.Count()),
                    errorsByDay = DayPoints(errorViews, days, g => g.Count()),
                    latencyByDay = DayPoints(views, days, g => AvgDuration(g)),
                    clientTypes = views.GroupBy(ClientType).Select(g => new { label = g.Key, value = g.Count() }).OrderByDescending(x => x.value).ToList(),
                    topEndpoints,
                    topUsers = userRows.OrderByDescending(x => x.requests).ToList()
                },
                assignments = new
                {
                    totals = new { totalAttempts = assignmentViews.Count, successRate = assignmentSuccessRate, codeAttempts = assignmentViews.Count(x => Contains(x.Path, "/solutions") || Contains(x.Path, "/submissions")), testAttempts = assignmentViews.Count(x => Contains(x.Path, "test")), imageAttempts = assignmentViews.Count(x => Contains(x.Path, "image")), mathAttempts = assignmentViews.Count(x => Contains(x.Path, "math")), avgTestScore = assignmentSuccessRate },
                    attemptsByDay = DayPoints(assignmentViews, days, g => g.Count()),
                    successByDay = DayPoints(successAssignmentViews, days, g => g.Count()),
                    types = assignmentViews.GroupBy(x => AssignmentTypeFromPath(x.Path)).Select(g => new { label = g.Key, value = g.Count() }).OrderByDescending(x => x.value).ToList(),
                    languages = views.Select(x => LanguageFromPath(x.Path)).Where(x => x != null).GroupBy(x => x!).Select(g => new { label = g.Key, value = g.Count() }).OrderByDescending(x => x.value).Take(10).ToList(),
                    topAssignments = assignmentRows,
                    hardAssignments
                },
                support = new
                {
                    totals = new { totalTickets = supportViews.Count, openTickets = supportViews.Count(x => !Contains(x.Path, "closed")), avgFirstResponseMinutes = 0, avgCloseMinutes = 0 },
                    ticketsByDay = DayPoints(supportViews, days, g => g.Count()),
                    closedByDay = DayPoints(supportViews.Where(x => Contains(x.Path, "closed") || Contains(x.Path, "resolved")), days, g => g.Count()),
                    ticketTypes = supportViews.GroupBy(x => Contains(x.Path, "admin") ? "Админка" : "Пользователь").Select(g => new { label = g.Key, value = g.Count() }).ToList(),
                    topAdmins = Array.Empty<object>()
                },
                executive = new
                {
                    comparisons = new object[]
                    {
                        Comparison("Активные пользователи", activeUserCount, prevActiveUserCount),
                        Comparison("Запросы API", views.Count, prevViews.Count),
                        Comparison("Ошибки API", errorViews.Count, prevViews.Count(IsError)),
                        Comparison("Средняя задержка", avgLatency, prevAvgLatency, "мс"),
                        Comparison("Успешность заданий", assignmentSuccessRate, prevSuccessRate, "%", true)
                    },
                    alerts = BuildAlerts(errorViews.Count, views.Count, avgLatency, assignmentSuccessRate),
                    noisyUsers = userRows.OrderByDescending(x => x.requests).Take(5),
                    slowEndpoints = topEndpoints.OrderByDescending(x => x.avgLatencyMs).Take(5),
                    failingAssignments = hardAssignments.Take(5)
                },
                alerts = BuildAlerts(errorViews.Count, views.Count, avgLatency, assignmentSuccessRate),
                comparison = new { previousRequests = prevViews.Count, requestDelta = views.Count - prevViews.Count }
            });
        });

        app.MapGet("/api/admin/analytics/users/search", async (ObservabilityDbContext db, IConfiguration cfg, IHttpClientFactory httpFactory, string? q, int take = 20, CancellationToken ct = default) =>
        {
            var query = NormalizeSearch(q);
            var rows = await db.PageViews.AsNoTracking().Where(x => x.UserId.HasValue).GroupBy(x => x.UserId).Select(g => new { userId = g.Key!.Value, lastSeenAt = g.Max(x => x.CreatedAt), requests = g.Count(), lastLoginAt = g.Where(x => x.Action == "login").Max(x => (DateTimeOffset?)x.CreatedAt) }).OrderByDescending(x => x.lastSeenAt).Take(500).ToListAsync(ct);
            var users = await LoadUserSummariesAsync(rows.Select(x => x.userId), cfg, httpFactory, ct);
            var result = rows.Select(x =>
            {
                var u = users.GetValueOrDefault(x.userId);
                return new { userId = x.userId, login = u?.Login, fullName = UserLabel(u), displayName = UserLabel(u), email = u?.Email ?? u?.MaskedEmail, role = u?.Role ?? "User", value = x.requests, requests = x.requests, lastSeenAt = x.lastSeenAt, lastLoginAt = x.lastLoginAt ?? x.lastSeenAt };
            }).Where(x => string.IsNullOrWhiteSpace(query) || NormalizeSearch($"{x.fullName} {x.email} {x.role}").Contains(query)).Take(System.Math.Clamp(take, 1, 100)).ToList();
            return Microsoft.AspNetCore.Http.Results.Ok(result);
        });

        app.MapGet("/api/admin/analytics/users/{userId:guid}", async (Guid userId, ObservabilityDbContext db, IConfiguration cfg, IHttpClientFactory httpFactory, int days = 30, CancellationToken ct = default) =>
        {
            days = System.Math.Clamp(days, 1, 365);
            var since = DateTimeOffset.UtcNow.AddDays(-days);
            var rows = await db.PageViews.AsNoTracking().Where(x => x.UserId == userId && x.CreatedAt >= since).OrderByDescending(x => x.CreatedAt).Take(1000).ToListAsync(ct);
            var users = await LoadUserSummariesAsync(new[] { userId }, cfg, httpFactory, ct);
            var u = users.GetValueOrDefault(userId);
            return Microsoft.AspNetCore.Http.Results.Ok(new
            {
                userId,
                periodDays = days,
                profile = new { userId, login = u?.Login, fullName = UserLabel(u), displayName = UserLabel(u), email = u?.Email ?? u?.MaskedEmail, role = u?.Role ?? "User", lastLoginAt = rows.Where(IsLogin).Select(x => x.CreatedAt).DefaultIfEmpty(rows.FirstOrDefault()?.CreatedAt ?? DateTimeOffset.MinValue).Max() },
                activity = new { requests = rows.Count, errors = rows.Count(IsError), avgLatencyMs = AvgDuration(rows), submissions = rows.Count(IsAssignmentActivity), ticketsCreated = rows.Count(x => Contains(x.Path, "/support")) },
                charts = new { loginsByDay = DayPoints(rows.Where(IsLogin), days, g => g.Count()), requestsByDay = DayPoints(rows, days, g => g.Count()), requestsByHour = HourPoints(rows) },
                topPaths = rows.GroupBy(x => NormalizeEndpoint(x.Path)).Select(g => new { label = g.Key, value = g.Count(), avgLatencyMs = AvgDuration(g), errorRate = Percent(g.Count(IsError), g.Count()) }).OrderByDescending(x => x.value).Take(20).ToList(),
                submissions = rows.Where(IsAssignmentActivity).Take(100).ToList(),
                logins = rows.Where(IsLogin).Take(100).ToList(),
                recentActivity = rows.Take(100).ToList()
            });
        });

        return app;
    }
}
