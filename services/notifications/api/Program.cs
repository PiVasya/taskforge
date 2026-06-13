using Microsoft.EntityFrameworkCore;
using TaskForge.Notifications.Api.Data;
using TaskForge.Notifications.Api.Domain;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddTaskForgeDebugDiagnostics("notifications-api");
builder.Services.AddTaskForgeRedisCache(builder.Configuration, "notifications-api");

builder.Services.AddHealthChecks();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddDbContext<NotificationsDbContext>(options =>
{
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection"));
});

var app = builder.Build();

app.UseTaskForgeDebugRequestLogging("notifications-api");

if (builder.Configuration.GetValue("Database:MigrateOnStartup", true))
{
    using var migrationScope = app.Services.CreateScope();
    var db = migrationScope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
    app.Logger.LogInformation("Applying EF Core migrations for NotificationsDbContext...");
    await db.Database.MigrateAsync();
    app.Logger.LogInformation("EF Core migrations for NotificationsDbContext applied.");
}
else if (builder.Configuration.GetValue("Database:EnsureCreated", false))
{
    using var ensureScope = app.Services.CreateScope();
    var db = ensureScope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
    await db.Database.EnsureCreatedAsync();
}


if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseTaskForgeRequestSecurity("notifications");

app.MapGet("/health/live", () => Results.Ok(new { status = "ok", service = "taskforge-notifications-api" }));
app.MapGet("/health/ready", async (NotificationsDbContext db) =>
{
    var canConnect = await db.Database.CanConnectAsync();
    return canConnect ? Results.Ok(new { status = "ready", service = "taskforge-notifications-api" }) : Results.StatusCode(503);
});
app.MapGet("/", () => Results.Ok(new
{
    service = "taskforge-notifications-api",
    database = "taskforge_notifications",
    migrations = "tracked EF Core migrations",
    status = "microservice boundary extracted"
}));
app.MapGet("/api/notifications/schema-owner", () => Results.Ok(new
{
    database = "taskforge_notifications",
    ownedEntities = new[] { "Notification", "NotificationSubscription", "NotificationOutboxMessage" }
}));


app.MapGet("/api/notifications", async (NotificationsDbContext db, Guid? userId, bool unreadOnly = false, int take = 100) =>
{
    var query = db.Notifications.AsNoTracking();
    if (userId.HasValue) query = query.Where(x => x.UserId == userId.Value);
    if (unreadOnly) query = query.Where(x => !x.IsRead);
    var rows = await query.OrderByDescending(x => x.CreatedAt).Take(Math.Clamp(take, 1, 500)).ToListAsync();
    return Results.Ok(rows.Select(ToDto).ToList());
});

app.MapPost("/api/notifications", async (NotificationRequest request, NotificationsDbContext db) =>
{
    var item = new NotificationItem
    {
        UserId = request.UserId,
        Type = string.IsNullOrWhiteSpace(request.Type) ? "system" : request.Type.Trim(),
        Title = string.IsNullOrWhiteSpace(request.Title) ? "Уведомление" : request.Title.Trim(),
        Message = request.Message
    };
    db.Notifications.Add(item);
    await db.SaveChangesAsync();
    return Results.Ok(ToDto(item));
});

app.MapPost("/api/notifications/{id:guid}/read", async (Guid id, NotificationsDbContext db) =>
{
    var item = await db.Notifications.FindAsync(id);
    if (item == null) return Results.NotFound();
    item.IsRead = true;
    await db.SaveChangesAsync();
    return Results.Ok(ToDto(item));
});

app.Run();

static object ToDto(NotificationItem x) => new { x.Id, x.UserId, x.Type, x.Title, x.Message, x.IsRead, x.CreatedAt };
public sealed record NotificationRequest(Guid? UserId, string? Type, string? Title, string? Message);
