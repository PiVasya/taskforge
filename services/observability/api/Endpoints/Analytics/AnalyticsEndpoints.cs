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
            var prevAssignmentViews = prevApiViews.Where(IsAssignmentActivity).ToList();
            var supportStats = await LoadSupportAnalyticsAsync(cfg, httpFactory, since, now, days, supportViews, ct);
            var learningStats = await LoadLearningAnalyticsAsync(cfg, httpFactory, since, now, days, assignmentViews, ct);
            var prevLearningStats = await LoadLearningAnalyticsAsync(cfg, httpFactory, prevSince, since, days, prevAssignmentViews, ct);
            var assignmentSuccessRate = learningStats.Totals.SuccessRate;
            var prevSuccessRate = prevLearningStats.Totals.SuccessRate;

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
                    rawIpStored = true,
                    ipPrefix = "IPv4 /24, IPv6 /48",
                    note = "IP фиксируются по правилам пользовательского соглашения; в этой аналитике они сгруппированы по подсетям."
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
                assignments = learningStats,
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
                    failingAssignments = learningStats.HardAssignments.Take(5)
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

        var userMessages = fallbackViews.Where(x => string.Equals(x.Method, "POST", StringComparison.OrdinalIgnoreCase) && !Contains(x.Path, "/admin")).ToList();
        var adminMessages = fallbackViews.Where(x => string.Equals(x.Method, "POST", StringComparison.OrdinalIgnoreCase) && Contains(x.Path, "/admin")).ToList();
        return new
        {
            totals = new { totalTickets = userMessages.Count, totalChats = userMessages.Count, totalMessages = userMessages.Count + adminMessages.Count, userMessages = userMessages.Count, adminMessages = adminMessages.Count, avgFirstResponseMinutes = 0, avgResponseMinutes = 0 },
            userMessagesByDay = DayPoints(userMessages, days, g => g.Count()),
            adminMessagesByDay = DayPoints(adminMessages, days, g => g.Count()),
            ticketsByDay = DayPoints(userMessages, days, g => g.Count()),
            closedByDay = DayPoints(adminMessages, days, g => g.Count()),
            ticketTypes = userMessages.Count == 0 ? Array.Empty<object>() : new[] { new { label = "Чаты", value = userMessages.Count } }.Cast<object>().ToArray(),
            topAdmins = Array.Empty<object>()
        };
    }

    private static async Task<LearningAnalyticsSnapshot> LoadLearningAnalyticsAsync(IConfiguration cfg, IHttpClientFactory httpFactory, DateTimeOffset fromUtc, DateTimeOffset toUtc, int days, List<PageView> fallbackViews, CancellationToken ct)
    {
        var snapshots = new List<LearningAnalyticsSnapshot>();
        var solutions = await LoadRemoteLearningAnalyticsAsync(cfg, httpFactory, "SolutionsApi", "http://solutions-api:8080", "/api/internal/solutions/analytics/summary", fromUtc, toUtc, days, ct);
        var tasks = await LoadRemoteLearningAnalyticsAsync(cfg, httpFactory, "TasksApi", "http://tasks-api:8080", "/api/internal/assignments/analytics/summary", fromUtc, toUtc, days, ct);
        if (solutions != null) snapshots.Add(solutions);
        if (tasks != null) snapshots.Add(tasks);
        return snapshots.Count == 0 ? EmptyLearningAnalytics(days) : MergeLearningAnalytics(snapshots, days);
    }

    private static async Task<LearningAnalyticsSnapshot?> LoadRemoteLearningAnalyticsAsync(IConfiguration cfg, IHttpClientFactory httpFactory, string serviceName, string fallbackUrl, string path, DateTimeOffset fromUtc, DateTimeOffset toUtc, int days, CancellationToken ct)
    {
        try
        {
            var url = $"{ServiceUrl(cfg, serviceName, fallbackUrl)}{path}?fromUtc={Uri.EscapeDataString(fromUtc.ToString("O"))}&toUtc={Uri.EscapeDataString(toUtc.ToString("O"))}&days={days}";
            var client = httpFactory.CreateClient();
            using var msg = new HttpRequestMessage(HttpMethod.Get, url);
            AddInternalKey(msg, cfg);
            using var resp = await client.SendAsync(msg, ct);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadFromJsonAsync<LearningAnalyticsSnapshot>(JsonOptions(), ct);
        }
        catch
        {
            return null;
        }
    }

    private static LearningAnalyticsSnapshot MergeLearningAnalytics(List<LearningAnalyticsSnapshot> snapshots, int days)
    {
        var totals = new LearningTotals
        {
            TotalAttempts = snapshots.Sum(x => x.Totals.TotalAttempts),
            PassedAttempts = snapshots.Sum(x => x.Totals.PassedAttempts),
            FailedAttempts = snapshots.Sum(x => x.Totals.FailedAttempts),
            CodeAttempts = snapshots.Sum(x => x.Totals.CodeAttempts),
            TestAttempts = snapshots.Sum(x => x.Totals.TestAttempts),
            ImageAttempts = snapshots.Sum(x => x.Totals.ImageAttempts),
            MathAttempts = snapshots.Sum(x => x.Totals.MathAttempts),
        };
        if (totals.FailedAttempts == 0 && totals.TotalAttempts > totals.PassedAttempts) totals.FailedAttempts = totals.TotalAttempts - totals.PassedAttempts;
        totals.SuccessRate = totals.TotalAttempts <= 0 ? 0 : System.Math.Round(totals.PassedAttempts * 100.0 / totals.TotalAttempts, 1);
        var scoredSnapshots = snapshots.Where(x => x.Totals.AvgTestScore > 0 && (x.Totals.TestAttempts + x.Totals.MathAttempts) > 0).ToList();
        totals.AvgTestScore = scoredSnapshots.Count == 0 ? 0 : System.Math.Round(scoredSnapshots.Sum(x => x.Totals.AvgTestScore * (x.Totals.TestAttempts + x.Totals.MathAttempts)) / scoredSnapshots.Sum(x => x.Totals.TestAttempts + x.Totals.MathAttempts), 1);

        var rows = snapshots.SelectMany(x => x.TopAssignments ?? new List<LearningAssignment>()).GroupBy(x => x.AssignmentId != Guid.Empty ? x.AssignmentId.ToString("N") : NormalizeSearch(x.Title)).Select(g =>
        {
            var first = g.First();
            var attempts = g.Sum(x => x.Attempts);
            var passed = g.Sum(x => x.Passed);
            var failed = g.Sum(x => x.Failed);
            if (failed == 0 && attempts > passed) failed = attempts - passed;
            return new LearningAssignment
            {
                AssignmentId = first.AssignmentId,
                Title = first.Title,
                CourseId = first.CourseId,
                CourseTitle = first.CourseTitle,
                Type = first.Type,
                Difficulty = first.Difficulty,
                Rating = first.Rating,
                Attempts = attempts,
                Passed = passed,
                Failed = failed,
                UniqueUsers = g.Sum(x => x.UniqueUsers),
                StuckUsers = g.Sum(x => x.StuckUsers),
                SuccessRate = attempts <= 0 ? 0 : System.Math.Round(passed * 100.0 / attempts, 1),
                Value = attempts,
            };
        }).ToList();

        return new LearningAnalyticsSnapshot
        {
            Totals = totals,
            AttemptsByDay = MergePoints(snapshots.SelectMany(x => x.AttemptsByDay ?? new List<LearningPoint>()), days),
            SuccessByDay = MergePoints(snapshots.SelectMany(x => x.SuccessByDay ?? new List<LearningPoint>()), days),
            FailureByDay = MergePoints(snapshots.SelectMany(x => x.FailureByDay ?? new List<LearningPoint>()), days),
            Types = MergeItems(snapshots.SelectMany(x => x.Types ?? new List<LearningItem>())),
            Languages = MergeItems(snapshots.SelectMany(x => x.Languages ?? new List<LearningItem>())).Take(12).ToList(),
            TopAssignments = rows.OrderByDescending(x => x.Attempts).Take(20).ToList(),
            HardAssignments = rows.Where(x => x.Attempts >= 2).OrderBy(x => x.SuccessRate).ThenByDescending(x => x.Failed).ThenByDescending(x => x.Attempts).Take(20).ToList(),
        };
    }

    private static LearningAnalyticsSnapshot EmptyLearningAnalytics(int days) => new()
    {
        Totals = new LearningTotals(),
        AttemptsByDay = EmptyLearningPoints(days),
        SuccessByDay = EmptyLearningPoints(days),
        FailureByDay = EmptyLearningPoints(days),
    };

    private static List<LearningPoint> EmptyLearningPoints(int days)
    {
        var start = DateTime.UtcNow.Date.AddDays(-(days - 1));
        return Enumerable.Range(0, days).Select(i =>
        {
            var day = start.AddDays(i);
            return new LearningPoint { Label = day.ToString("dd.MM"), Date = day.ToString("yyyy-MM-dd"), Value = 0, Count = 0 };
        }).ToList();
    }

    private static List<LearningPoint> MergePoints(IEnumerable<LearningPoint> points, int days)
    {
        var empty = EmptyLearningPoints(days).ToDictionary(x => x.Date, x => x);
        foreach (var p in points)
        {
            var key = string.IsNullOrWhiteSpace(p.Date) ? p.Label : p.Date;
            if (string.IsNullOrWhiteSpace(key)) continue;
            if (!empty.TryGetValue(key, out var row))
            {
                row = new LearningPoint { Label = p.Label ?? key, Date = key };
                empty[key] = row;
            }
            row.Value += p.Value != 0 ? p.Value : p.Count;
            row.Count = row.Value;
        }
        return empty.Values.OrderBy(x => x.Date).ToList();
    }

    private static List<LearningItem> MergeItems(IEnumerable<LearningItem> items) => items
        .Where(x => !string.IsNullOrWhiteSpace(x.Label))
        .GroupBy(x => x.Label.Trim())
        .Select(g => new LearningItem { Label = g.Key, Value = g.Sum(x => x.Value) })
        .OrderByDescending(x => x.Value)
        .ToList();

    public sealed class LearningAnalyticsSnapshot
    {
        public LearningTotals Totals { get; set; } = new();
        public List<LearningPoint> AttemptsByDay { get; set; } = new();
        public List<LearningPoint> SuccessByDay { get; set; } = new();
        public List<LearningPoint> FailureByDay { get; set; } = new();
        public List<LearningItem> Types { get; set; } = new();
        public List<LearningItem> Languages { get; set; } = new();
        public List<LearningAssignment> TopAssignments { get; set; } = new();
        public List<LearningAssignment> HardAssignments { get; set; } = new();
    }

    public sealed class LearningTotals
    {
        public int TotalAttempts { get; set; }
        public int PassedAttempts { get; set; }
        public int FailedAttempts { get; set; }
        public double SuccessRate { get; set; }
        public int CodeAttempts { get; set; }
        public int TestAttempts { get; set; }
        public int ImageAttempts { get; set; }
        public int MathAttempts { get; set; }
        public double AvgTestScore { get; set; }
    }

    public sealed class LearningPoint
    {
        public string Label { get; set; } = string.Empty;
        public string Date { get; set; } = string.Empty;
        public double Value { get; set; }
        public double Count { get; set; }
    }

    public sealed class LearningItem
    {
        public string Label { get; set; } = string.Empty;
        public int Value { get; set; }
    }

    public sealed class LearningAssignment
    {
        public Guid AssignmentId { get; set; }
        public Guid? CourseId { get; set; }
        public string? CourseTitle { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public int Difficulty { get; set; }
        public int Rating { get; set; }
        public int Attempts { get; set; }
        public int Passed { get; set; }
        public int Failed { get; set; }
        public int UniqueUsers { get; set; }
        public int StuckUsers { get; set; }
        public double SuccessRate { get; set; }
        public int Value { get; set; }
    }

}
