using Microsoft.EntityFrameworkCore;
using TaskForge.Notifications.Api.Data;
using TaskForge.Notifications.Api.Domain;

using TaskForge.Notifications.Api.Endpoints;
using static TaskForge.Notifications.Api.Services.Common.NotificationsApiCommonService;
using static TaskForge.Notifications.Api.Services.Mapping.NotificationsApiMappingService;

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

app.MapNotificationsApiEndpoints();

app.Run();
