using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TaskForge.Observability.Api.Data;
using TaskForge.Observability.Api.Domain;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddTaskForgeDebugDiagnostics("observability-api");
builder.Services.AddTaskForgeRedisCache(builder.Configuration, "observability-api");
builder.Services.AddHealthChecks();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHttpClient();
builder.Services.AddDbContext<ObservabilityDbContext>(options => options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));
var app = builder.Build();

app.UseTaskForgeDebugRequestLogging("observability-api");
if (builder.Configuration.GetValue("Database:MigrateOnStartup", true)) { using var s = app.Services.CreateScope(); var db = s.ServiceProvider.GetRequiredService<ObservabilityDbContext>(); app.Logger.LogInformation("Applying EF Core migrations for ObservabilityDbContext..."); await db.Database.MigrateAsync(); app.Logger.LogInformation("EF Core migrations for ObservabilityDbContext applied."); }
else if (builder.Configuration.GetValue("Database:EnsureCreated", false)) { using var s = app.Services.CreateScope(); await s.ServiceProvider.GetRequiredService<ObservabilityDbContext>().Database.EnsureCreatedAsync(); }
if (app.Environment.IsDevelopment()) { app.UseSwagger(); app.UseSwaggerUI(); }
app.UseTaskForgeRequestSecurity("observability");
app.MapGet("/health/live", () => Results.Ok(new { status = "ok", service = "taskforge-observability-api" }));
app.MapGet("/health/ready", async (ObservabilityDbContext db) => await db.Database.CanConnectAsync() ? Results.Ok(new { status = "ready", service = "taskforge-observability-api" }) : Results.StatusCode(503));
app.MapGet("/", () => Results.Ok(new { service = "taskforge-observability-api", database = "taskforge_observability", status = "observability microservice active" }));
app.MapGet("/api/observability/schema-owner", () => Results.Ok(new { database = "taskforge_observability", ownedEntities = new[] { "PageView", "AuditLog" } }));

app.MapPost("/api/activity/page-view", async (PageViewRequest req, HttpContext http, IConfiguration cfg, ObservabilityDbContext db, CancellationToken ct) =>
{
    var view = new PageView
    {
        UserId = TaskForgeRequestSecurity.UserId(http, cfg),
        Path = req.Path ?? req.Url ?? http.Request.Headers.Referer.ToString() ?? "/",
        Method = req.Method ?? "GET",
        Action = req.Action ?? "page-view",
        StatusCode = req.StatusCode,
        DurationMs = req.DurationMs,
        UserAgent = http.Request.Headers.UserAgent.ToString()
    };
    db.PageViews.Add(view);
    await db.SaveChangesAsync(ct);
    return Results.Ok(new { saved = true, view.Id });
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
    days = Math.Clamp(days, 1, 365);
    page = Math.Max(1, page);
    pageSize = Math.Clamp(take ?? pageSize, 10, 1000);

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

    return Results.Ok(new
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
app.MapGet("/api/admin/system-status", () => Results.Ok(new { status = "ok", services = new[] { "identity", "education", "content", "tasks", "quiz", "solutions", "execution", "ai", "support", "minecraft", "files", "notifications", "observability" }, generatedAt = DateTimeOffset.UtcNow }));
app.MapGet("/api/system-status", () => Results.Ok(new { status = "ok", generatedAt = DateTimeOffset.UtcNow }));

app.MapGet("/api/admin/analytics/overview", async (ObservabilityDbContext db, IConfiguration cfg, IHttpClientFactory httpFactory, int days = 30, CancellationToken ct = default) =>
{
    days = Math.Clamp(days, 1, 365);
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
    var assignmentSuccessRate = Percent(successAssignmentViews.Count, Math.Max(1, assignmentViews.Count));
    var prevAssignmentViews = prevViews.Where(IsAssignmentActivity).ToList();
    var prevSuccessRate = Percent(prevAssignmentViews.Count(IsSuccess), Math.Max(1, prevAssignmentViews.Count));

    var userRows = views.Where(x => x.UserId.HasValue).GroupBy(x => x.UserId!.Value).Select(g =>
    {
        var u = users.GetValueOrDefault(g.Key);
        return new
        {
            userId = g.Key,
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

    return Results.Ok(new
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
        return new { userId = x.userId, fullName = UserLabel(u), displayName = UserLabel(u), email = u?.Email ?? u?.MaskedEmail, role = u?.Role ?? "User", value = x.requests, requests = x.requests, lastSeenAt = x.lastSeenAt, lastLoginAt = x.lastLoginAt ?? x.lastSeenAt };
    }).Where(x => string.IsNullOrWhiteSpace(query) || NormalizeSearch($"{x.fullName} {x.email} {x.role}").Contains(query)).Take(Math.Clamp(take, 1, 100)).ToList();
    return Results.Ok(result);
});

app.MapGet("/api/admin/analytics/users/{userId:guid}", async (Guid userId, ObservabilityDbContext db, IConfiguration cfg, IHttpClientFactory httpFactory, int days = 30, CancellationToken ct = default) =>
{
    days = Math.Clamp(days, 1, 365);
    var since = DateTimeOffset.UtcNow.AddDays(-days);
    var rows = await db.PageViews.AsNoTracking().Where(x => x.UserId == userId && x.CreatedAt >= since).OrderByDescending(x => x.CreatedAt).Take(1000).ToListAsync(ct);
    var users = await LoadUserSummariesAsync(new[] { userId }, cfg, httpFactory, ct);
    var u = users.GetValueOrDefault(userId);
    return Results.Ok(new
    {
        userId,
        periodDays = days,
        profile = new { userId, fullName = UserLabel(u), displayName = UserLabel(u), email = u?.Email ?? u?.MaskedEmail, role = u?.Role ?? "User", lastLoginAt = rows.Where(IsLogin).Select(x => x.CreatedAt).DefaultIfEmpty(rows.FirstOrDefault()?.CreatedAt ?? DateTimeOffset.MinValue).Max() },
        activity = new { requests = rows.Count, errors = rows.Count(IsError), avgLatencyMs = AvgDuration(rows), submissions = rows.Count(IsAssignmentActivity), ticketsCreated = rows.Count(x => Contains(x.Path, "/support")) },
        charts = new { loginsByDay = DayPoints(rows.Where(IsLogin), days, g => g.Count()), requestsByDay = DayPoints(rows, days, g => g.Count()), requestsByHour = HourPoints(rows) },
        topPaths = rows.GroupBy(x => NormalizeEndpoint(x.Path)).Select(g => new { label = g.Key, value = g.Count(), avgLatencyMs = AvgDuration(g), errorRate = Percent(g.Count(IsError), g.Count()) }).OrderByDescending(x => x.value).Take(20).ToList(),
        submissions = rows.Where(IsAssignmentActivity).Take(100).ToList(),
        logins = rows.Where(IsLogin).Take(100).ToList(),
        recentActivity = rows.Take(100).ToList()
    });
});
app.Run();

static string ServiceUrl(IConfiguration cfg, string name, string fallback) => (cfg[$"Services:{name}"] ?? cfg[$"ServiceUrls:{name}"] ?? fallback).TrimEnd('/');
static void AddInternalKey(HttpRequestMessage msg, IConfiguration cfg)
{
    var key = cfg["InternalApi:Key"] ?? cfg["TaskForgeInternalApi:ApiKey"] ?? cfg["TaskForge:InternalKey"] ?? Environment.GetEnvironmentVariable("TASKFORGE_INTERNAL_KEY");
    if (!string.IsNullOrWhiteSpace(key)) msg.Headers.TryAddWithoutValidation("X-Internal-Key", key);
}
static JsonSerializerOptions JsonOptions() => new(JsonSerializerDefaults.Web) { WriteIndented = false };
static async Task<Dictionary<Guid, UserSummaryDto>> LoadUserSummariesAsync(IEnumerable<Guid> userIds, IConfiguration cfg, IHttpClientFactory httpFactory, CancellationToken ct)
{
    var ids = userIds.Where(x => x != Guid.Empty).Distinct().Take(1000).ToArray();
    if (ids.Length == 0) return new Dictionary<Guid, UserSummaryDto>();
    TaskForgeDebugTrace.UserSummaryRequest("observability-api", "identity-api", ids);
    try
    {
        var client = httpFactory.CreateClient();
        using var msg = new HttpRequestMessage(HttpMethod.Post, $"{ServiceUrl(cfg, "IdentityApi", "http://identity-api:8080")}/api/internal/users/summaries")
        {
            Content = JsonContent.Create(new UserIdsRequest(ids), options: JsonOptions())
        };
        AddInternalKey(msg, cfg);
        using var resp = await client.SendAsync(msg, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var empty = new Dictionary<Guid, UserSummaryDto>();
            TaskForgeDebugTrace.UserSummaryResponse("observability-api", "identity-api", ids, empty);
            return empty;
        }
        var rows = await resp.Content.ReadFromJsonAsync<List<UserSummaryDto>>(JsonOptions(), ct) ?? new List<UserSummaryDto>();
        var map = rows.Select(x => { x.Normalize(); return x; }).Where(x => x.UserId != Guid.Empty).GroupBy(x => x.UserId).ToDictionary(x => x.Key, x => x.First());
        TaskForgeDebugTrace.UserSummaryResponse("observability-api", "identity-api", ids, map);
        return map;
    }
    catch
    {
        var empty = new Dictionary<Guid, UserSummaryDto>();
        TaskForgeDebugTrace.UserSummaryResponse("observability-api", "identity-api", ids, empty);
        return empty;
    }
}
static ActivityItemDto ToActivityItem(PageView view, UserSummaryDto? user)
{
    var category = ActivityCategory(view);
    var actionType = ActivityActionType(view);
    var target = NormalizeEndpoint(view.Path);
    var userDto = view.UserId.HasValue
        ? new ActivityUserDto
        {
            Id = view.UserId.Value,
            FullName = UserLabel(user),
            DisplayName = UserLabel(user),
            Email = user?.Email ?? user?.MaskedEmail,
            Role = user?.Role ?? "User",
        }
        : null;

    return new ActivityItemDto
    {
        Id = view.Id,
        UserId = view.UserId,
        Category = category,
        ActionType = actionType,
        Source = ActivitySource(view),
        Method = view.Method,
        Path = view.Path,
        Target = target,
        Description = ActivityDescription(view, category, actionType, target),
        StatusCode = view.StatusCode,
        IsAuthenticated = view.UserId.HasValue,
        CreatedAtUtc = view.CreatedAt,
        DurationMs = view.DurationMs,
        User = userDto,
    };
}
static string ActivityCategory(PageView view)
{
    if (IsError(view)) return "error";
    if (Contains(view.Path, "/admin")) return "admin";
    if (IsLogin(view)) return "auth";
    if (IsAssignmentActivity(view)) return "assignment";
    if (Contains(view.Path, "/support")) return "support";
    if (string.Equals(view.Action, "page-view", StringComparison.OrdinalIgnoreCase) || string.Equals(view.Method, "GET", StringComparison.OrdinalIgnoreCase)) return "navigation";
    return "api";
}
static string ActivityActionType(PageView view)
{
    if (!string.IsNullOrWhiteSpace(view.Action)) return view.Action!;
    if (!string.IsNullOrWhiteSpace(view.Method)) return view.Method!.ToUpperInvariant();
    return "event";
}
static string ActivitySource(PageView view) => ClientType(view);
static string ActivityDescription(PageView view, string category, string actionType, string target)
{
    var method = string.IsNullOrWhiteSpace(view.Method) ? "REQUEST" : view.Method!.ToUpperInvariant();
    var status = view.StatusCode.HasValue ? $", статус {view.StatusCode.Value}" : string.Empty;
    return category switch
    {
        "admin" => $"Действие в админке: {actionType} {target}{status}",
        "auth" => $"Действие авторизации: {actionType} {target}{status}",
        "assignment" => $"Активность по заданию: {actionType} {target}{status}",
        "support" => $"Активность поддержки: {actionType} {target}{status}",
        "error" => $"Ошибка запроса: {method} {target}{status}",
        "navigation" => $"Переход по странице: {target}{status}",
        _ => $"{method} {target}{status}",
    };
}
static string UserLabel(UserSummaryDto? user)
{
    var name = (user?.DisplayName ?? string.Empty).Trim();
    if (!string.IsNullOrWhiteSpace(name) && !LooksLikeEmail(name)) return name;
    var full = string.Join(' ', new[] { user?.FirstName, user?.LastName }.Where(x => !string.IsNullOrWhiteSpace(x))).Trim();
    if (!string.IsNullOrWhiteSpace(full)) return full;
    var masked = (user?.MaskedEmail ?? string.Empty).Trim();
    if (!string.IsNullOrWhiteSpace(masked)) return masked;
    return "Пользователь";
}
static bool LooksLikeEmail(string value) => value.Contains('@') && value.Contains('.');
static string NormalizeSearch(string? value) => string.Join(' ', (value ?? string.Empty).Trim().ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
static bool Contains(string? value, string term) => (value ?? string.Empty).Contains(term, StringComparison.OrdinalIgnoreCase);
static bool IsError(PageView v) => v.StatusCode is >= 400;
static bool IsSuccess(PageView v) => v.StatusCode is null or >= 200 and < 300;
static bool IsLogin(PageView v) => string.Equals(v.Action, "login", StringComparison.OrdinalIgnoreCase) || Contains(v.Path, "/login");
static bool IsAssignmentActivity(PageView v) => Contains(v.Path, "assignment") || Contains(v.Path, "submit") || Contains(v.Path, "attempt") || Contains(v.Path, "task-test") || Contains(v.Path, "math-task") || Contains(v.Path, "image-test") || Contains(v.Path, "solutions");
static string NormalizeEndpoint(string? path)
{
    var p = (path ?? "/").Split('?', '#')[0];
    var parts = p.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(x => Guid.TryParse(x, out _) ? "{id}" : x.Length > 24 ? "{value}" : x);
    return "/" + string.Join('/', parts);
}
static string ClientType(PageView v)
{
    if (Contains(v.Path, "/admin")) return "Админка";
    if (Contains(v.UserAgent, "bot")) return "Бот";
    if (string.IsNullOrWhiteSpace(v.UserAgent)) return "Внутренний клиент";
    return "Web";
}
static string AssignmentTypeFromPath(string? path)
{
    if (Contains(path, "math")) return "math";
    if (Contains(path, "image")) return "image";
    if (Contains(path, "test")) return "test";
    return "code";
}
static string AssignmentLabel(string? path)
{
    var p = NormalizeEndpoint(path);
    return string.IsNullOrWhiteSpace(p) ? "Задание без названия" : p;
}
static string? LanguageFromPath(string? path)
{
    var s = (path ?? string.Empty).ToLowerInvariant();
    foreach (var lang in new[] { "csharp", "cpp", "python", "javascript", "java", "pascal" }) if (s.Contains(lang)) return lang;
    return null;
}
static double AvgDuration(IEnumerable<PageView> rows)
{
    var vals = rows.Select(x => x.DurationMs).Where(x => x.HasValue).Select(x => (double)x!.Value).ToList();
    return vals.Count == 0 ? 0 : Math.Round(vals.Average(), 1);
}
static double PercentileDuration(IEnumerable<PageView> rows, double p)
{
    var vals = rows.Select(x => x.DurationMs).Where(x => x.HasValue).Select(x => (double)x!.Value).OrderBy(x => x).ToList();
    if (vals.Count == 0) return 0;
    var idx = Math.Clamp((int)Math.Ceiling(p * vals.Count) - 1, 0, vals.Count - 1);
    return vals[idx];
}
static double Percent(int num, int den) => den <= 0 ? 0 : Math.Round(num * 100.0 / den, 1);
static object Point(DateTime date, double value) => new { label = date.ToString("dd.MM"), date = date.ToString("yyyy-MM-dd"), value = Math.Round(value, 1), count = Math.Round(value, 1) };
static List<object> DayPoints(IEnumerable<PageView> rows, int days, Func<IEnumerable<PageView>, double> selector)
{
    var byDay = rows.GroupBy(x => x.CreatedAt.UtcDateTime.Date).ToDictionary(x => x.Key, x => (IEnumerable<PageView>)x.ToList());
    var start = DateTime.UtcNow.Date.AddDays(-(days - 1));
    return Enumerable.Range(0, days).Select(i => { var day = start.AddDays(i); return Point(day, byDay.TryGetValue(day, out var vals) ? selector(vals) : 0); }).ToList();
}
static List<object> HourPoints(IEnumerable<PageView> rows)
{
    var byHour = rows.GroupBy(x => x.CreatedAt.UtcDateTime.Hour).ToDictionary(x => x.Key, x => x.Count());
    return Enumerable.Range(0, 24).Select(h => new { label = $"{h:00}:00", value = byHour.GetValueOrDefault(h), count = byHour.GetValueOrDefault(h) }).Cast<object>().ToList();
}
static object Comparison(string label, double current, double previous, string? unit = null, bool percentMetric = false) => new { label, current, previous, unit, percentMetric, deltaPercent = previous == 0 ? (current == 0 ? 0 : 100) : Math.Round((current - previous) * 100.0 / previous, 1) };
static object[] BuildAlerts(int errors, int total, double avgLatency, double successRate)
{
    var errorRate = Percent(errors, Math.Max(1, total));
    var alerts = new List<object>();
    if (errorRate > 10) alerts.Add(new { severity = "high", title = "Много ошибок API", message = $"За период {errorRate:0.0}% запросов завершились ошибкой." });
    if (avgLatency > 1000) alerts.Add(new { severity = "medium", title = "Высокая задержка", message = $"Средняя задержка backend около {avgLatency:0} мс." });
    if (successRate > 0 && successRate < 35) alerts.Add(new { severity = "medium", title = "Низкая успешность заданий", message = $"Успешность попыток по заданиям {successRate:0.0}%." });
    if (alerts.Count == 0) alerts.Add(new { severity = "good", title = "Критичных сигналов нет", message = "По собранной активности явных проблем не найдено." });
    return alerts.ToArray();
}

public sealed class ActivityUserDto
{
    public Guid Id { get; set; }
    public string FullName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string? Role { get; set; }
}
public sealed class ActivityItemDto
{
    public Guid Id { get; set; }
    public Guid? UserId { get; set; }
    public string Category { get; set; } = string.Empty;
    public string ActionType { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string? Method { get; set; }
    public string Path { get; set; } = string.Empty;
    public string? Target { get; set; }
    public string? Description { get; set; }
    public int? StatusCode { get; set; }
    public bool IsAuthenticated { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public long? DurationMs { get; set; }
    public ActivityUserDto? User { get; set; }
}
public sealed record PageViewRequest(string? Path, string? Url, string? Method, string? Action, int? StatusCode, long? DurationMs);
public sealed record UserIdsRequest(Guid[] UserIds);
public sealed class UserSummaryDto
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string? Email { get; set; }
    public string? MaskedEmail { get; set; }
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? DisplayName { get; set; }
    public string? Role { get; set; }
    public void Normalize()
    {
        if (UserId == Guid.Empty) UserId = Id;
        if (string.IsNullOrWhiteSpace(DisplayName)) DisplayName = string.Join(' ', new[] { FirstName, LastName }.Where(x => !string.IsNullOrWhiteSpace(x))).Trim();
        if (string.IsNullOrWhiteSpace(DisplayName)) DisplayName = MaskedEmail;
    }
}
