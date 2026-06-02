using Microsoft.EntityFrameworkCore;
using TaskForge.Observability.Api.Data;
using TaskForge.Observability.Api.Domain;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHealthChecks();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddDbContext<ObservabilityDbContext>(options => options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));
var app = builder.Build();
if (builder.Configuration.GetValue("Database:MigrateOnStartup", true)) { using var s = app.Services.CreateScope(); var db = s.ServiceProvider.GetRequiredService<ObservabilityDbContext>(); app.Logger.LogInformation("Applying EF Core migrations for ObservabilityDbContext..."); await db.Database.MigrateAsync(); app.Logger.LogInformation("EF Core migrations for ObservabilityDbContext applied."); }
else if (builder.Configuration.GetValue("Database:EnsureCreated", false)) { using var s = app.Services.CreateScope(); await s.ServiceProvider.GetRequiredService<ObservabilityDbContext>().Database.EnsureCreatedAsync(); }
if (app.Environment.IsDevelopment()) { app.UseSwagger(); app.UseSwaggerUI(); }
app.UseTaskForgeRequestSecurity("observability");
app.MapGet("/health/live", () => Results.Ok(new { status = "ok", service = "taskforge-observability-api" }));
app.MapGet("/health/ready", async (ObservabilityDbContext db) => await db.Database.CanConnectAsync() ? Results.Ok(new { status = "ready", service = "taskforge-observability-api" }) : Results.StatusCode(503));
app.MapGet("/", () => Results.Ok(new { service = "taskforge-observability-api", database = "taskforge_observability", status = "observability microservice active" }));
app.MapGet("/api/observability/schema-owner", () => Results.Ok(new { database = "taskforge_observability", ownedEntities = new[] { "PageView", "AuditLog" } }));

app.MapPost("/api/activity/page-view", async (PageViewRequest req, HttpContext http, IConfiguration cfg, ObservabilityDbContext db) =>
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
    await db.SaveChangesAsync();
    return Results.Ok(new { saved = true, view.Id });
});

app.MapGet("/api/admin/activity", async (ObservabilityDbContext db, Guid? userId, string? query, int take = 200) =>
{
    var q = db.PageViews.AsNoTracking();
    if (userId.HasValue) q = q.Where(x => x.UserId == userId);
    if (!string.IsNullOrWhiteSpace(query)) q = q.Where(x => x.Path.ToLower().Contains(query.ToLower()) || (x.Action != null && x.Action.ToLower().Contains(query.ToLower())));
    return Results.Ok(await q.OrderByDescending(x => x.CreatedAt).Take(Math.Clamp(take, 1, 1000)).ToListAsync());
});
app.MapGet("/api/admin/system-status", () => Results.Ok(new { status = "ok", services = new[] { "identity", "education", "content", "tasks", "quiz", "solutions", "execution", "ai", "support", "minecraft", "files", "notifications", "observability" }, generatedAt = DateTimeOffset.UtcNow }));
app.MapGet("/api/system-status", () => Results.Ok(new { status = "ok", generatedAt = DateTimeOffset.UtcNow }));
app.MapGet("/api/admin/analytics/overview", async (ObservabilityDbContext db, int days = 30) =>
{
    var since = DateTimeOffset.UtcNow.AddDays(-Math.Clamp(days, 1, 365));
    var prevSince = since.AddDays(-Math.Clamp(days, 1, 365));
    var views = await db.PageViews.AsNoTracking().Where(x => x.CreatedAt >= since).ToListAsync();
    var prev = await db.PageViews.AsNoTracking().Where(x => x.CreatedAt >= prevSince && x.CreatedAt < since).CountAsync();
    var byDay = views.GroupBy(x => x.CreatedAt.UtcDateTime.Date).OrderBy(x => x.Key).Select(x => new { date = x.Key.ToString("yyyy-MM-dd"), count = x.Count() }).ToList();
    var usersByDay = views.Where(x => x.UserId.HasValue).GroupBy(x => x.CreatedAt.UtcDateTime.Date).OrderBy(x => x.Key).Select(x => new { date = x.Key.ToString("yyyy-MM-dd"), count = x.Select(v => v.UserId).Distinct().Count() }).ToList();
    var errorViews = views.Where(x => x.StatusCode >= 400).ToList();
    var errorsByDay = errorViews.GroupBy(x => x.CreatedAt.UtcDateTime.Date).OrderBy(x => x.Key).Select(x => new { date = x.Key.ToString("yyyy-MM-dd"), count = x.Count() }).ToList();
    return Results.Ok(new
    {
        periodDays = days,
        users = new { total = views.Where(x => x.UserId.HasValue).Select(x => x.UserId).Distinct().Count(), newByDay = usersByDay, loginsByDay = usersByDay },
        api = new { requests = views.Count, errors = errorViews.Count, requestsByDay = byDay, errorsByDay, topPaths = views.GroupBy(x => x.Path).OrderByDescending(x => x.Count()).Take(20).Select(x => new { path = x.Key, count = x.Count() }).ToList() },
        assignments = new { submissions = views.Count(x => x.Path.Contains("/submit")), attemptsByDay = byDay.Where(x => x.count > 0).ToList() },
        support = new { tickets = views.Count(x => x.Path.Contains("/support")), ticketsByDay = byDay },
        alerts = errorViews.Take(20).Select(x => new { x.Path, x.StatusCode, x.CreatedAt }).ToList(),
        comparison = new { previousRequests = prev, requestDelta = views.Count - prev }
    });
});
app.MapGet("/api/admin/analytics/users/search", async (ObservabilityDbContext db, string? q, int take = 20) =>
{
    var users = await db.PageViews.AsNoTracking().Where(x => x.UserId.HasValue).GroupBy(x => x.UserId).Select(g => new { userId = g.Key, lastSeenAt = g.Max(x => x.CreatedAt), requests = g.Count() }).OrderByDescending(x => x.lastSeenAt).Take(Math.Clamp(take, 1, 100)).ToListAsync();
    return Results.Ok(users);
});
app.MapGet("/api/admin/analytics/users/{userId:guid}", async (Guid userId, ObservabilityDbContext db, int days = 30) =>
{
    var since = DateTimeOffset.UtcNow.AddDays(-Math.Clamp(days, 1, 365));
    var rows = await db.PageViews.AsNoTracking().Where(x => x.UserId == userId && x.CreatedAt >= since).OrderByDescending(x => x.CreatedAt).Take(500).ToListAsync();
    return Results.Ok(new { userId, periodDays = days, activity = rows, submissions = rows.Where(x => x.Path.Contains("/submit")).ToList(), logins = rows.Where(x => x.Action == "login").ToList() });
});
app.Run();
public sealed record PageViewRequest(string? Path, string? Url, string? Method, string? Action, int? StatusCode, long? DurationMs);
