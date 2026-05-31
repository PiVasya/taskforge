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
app.MapGet("/health/live", () => Results.Ok(new { status = "ok", service = "taskforge-observability-api" }));
app.MapGet("/health/ready", async (ObservabilityDbContext db) => await db.Database.CanConnectAsync() ? Results.Ok(new { status = "ready", service = "taskforge-observability-api" }) : Results.StatusCode(503));
app.MapGet("/", () => Results.Ok(new { service = "taskforge-observability-api", database = "taskforge_observability", status = "observability microservice active" }));
app.MapGet("/api/observability/schema-owner", () => Results.Ok(new { database = "taskforge_observability", ownedEntities = new[] { "PageView", "AuditLog" } }));
app.MapPost("/api/activity/page-view", async (PageViewRequest req, ObservabilityDbContext db) => { var view = new PageView { Path = req.Path ?? req.Url ?? "/" }; db.PageViews.Add(view); await db.SaveChangesAsync(); return Results.Ok(new { saved = true, view.Id }); });
app.MapGet("/api/admin/activity", async (ObservabilityDbContext db) => Results.Ok(await db.PageViews.AsNoTracking().OrderByDescending(x => x.CreatedAt).Take(200).ToListAsync()));
app.MapGet("/api/admin/system-status", () => Results.Ok(new { status = "ok", services = "see docker compose ps", generatedAt = DateTimeOffset.UtcNow }));
app.MapGet("/api/system-status", () => Results.Ok(new { status = "ok", generatedAt = DateTimeOffset.UtcNow }));
app.MapGet("/api/admin/analytics/overview", async (ObservabilityDbContext db) => Results.Ok(new { pageViews = await db.PageViews.CountAsync(), users = 0, submissions = 0 }));
app.MapGet("/api/admin/analytics/users/search", () => Results.Ok(Array.Empty<object>()));
app.MapGet("/api/admin/analytics/users/{userId:guid}", (Guid userId) => Results.Ok(new { userId, activity = Array.Empty<object>() }));
app.Run();
public sealed record PageViewRequest(string? Path, string? Url);
