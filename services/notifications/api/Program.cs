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


app.MapGet("/api/notifications", async (HttpContext http, IConfiguration cfg, NotificationsDbContext db, Guid? userId, bool unreadOnly = false, int take = 100) =>
{
    var currentUserId = TaskForgeRequestSecurity.UserId(http, cfg);
    if (!currentUserId.HasValue) return Results.Unauthorized();

    var isAdmin = IsAdmin(http, cfg);
    var effectiveUserId = isAdmin && userId.HasValue ? userId.Value : currentUserId.Value;

    var query = db.Notifications.AsNoTracking().Where(x => x.UserId == effectiveUserId);
    if (unreadOnly) query = query.Where(x => !x.IsRead);

    var rows = await query
        .OrderByDescending(x => x.CreatedAt)
        .Take(Math.Clamp(take, 1, 500))
        .ToListAsync();

    return Results.Ok(rows.Select(ToDto).ToList());
});

app.MapPost("/api/internal/notifications", async (NotificationRequest request, NotificationsDbContext db) => await CreateNotification(request, db));

app.MapPost("/api/notifications", async (NotificationRequest request, HttpContext http, IConfiguration cfg, NotificationsDbContext db) =>
{
    if (!IsAdmin(http, cfg)) return Forbidden("Создавать уведомления может только администратор или внутренний сервис.");
    return await CreateNotification(request, db);
});

app.MapPost("/api/notifications/{id:guid}/read", async (Guid id, HttpContext http, IConfiguration cfg, NotificationsDbContext db) =>
{
    var currentUserId = TaskForgeRequestSecurity.UserId(http, cfg);
    if (!currentUserId.HasValue) return Results.Unauthorized();

    var isAdmin = IsAdmin(http, cfg);
    var item = await db.Notifications.FirstOrDefaultAsync(x => x.Id == id && (isAdmin || x.UserId == currentUserId.Value));
    if (item == null) return Results.NotFound();

    item.IsRead = true;
    await db.SaveChangesAsync();
    return Results.Ok(ToDto(item));
});

app.Run();

static async Task<IResult> CreateNotification(NotificationRequest request, NotificationsDbContext db)
{
    if (!request.UserId.HasValue || request.UserId.Value == Guid.Empty)
    {
        return Results.BadRequest(new { message = "UserId is required.", code = "USER_ID_REQUIRED" });
    }

    var item = new NotificationItem
    {
        UserId = request.UserId.Value,
        Type = string.IsNullOrWhiteSpace(request.Type) ? "system" : request.Type.Trim(),
        Title = string.IsNullOrWhiteSpace(request.Title) ? "Уведомление" : request.Title.Trim(),
        Message = request.Message
    };
    db.Notifications.Add(item);
    await db.SaveChangesAsync();
    return Results.Ok(ToDto(item));
}

static bool IsAdmin(HttpContext http, IConfiguration cfg)
{
    var principal = http.User?.Identity?.IsAuthenticated == true ? http.User : TaskForgeRequestSecurity.ValidateUser(http, cfg);
    return principal is not null && TaskForgeRequestSecurity.HasAnyRole(principal, "Admin");
}

static IResult Forbidden(string message) => Results.Json(new { message, code = "FORBIDDEN" }, statusCode: StatusCodes.Status403Forbidden);

static object ToDto(NotificationItem x) => new { x.Id, x.UserId, x.Type, x.Title, x.Message, x.IsRead, x.CreatedAt };
public sealed record NotificationRequest(Guid? UserId, string? Type, string? Title, string? Message);
