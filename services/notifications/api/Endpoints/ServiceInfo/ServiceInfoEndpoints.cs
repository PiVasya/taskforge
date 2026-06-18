using Microsoft.EntityFrameworkCore;
using TaskForge.Notifications.Api.Data;
using TaskForge.Notifications.Api.Domain;

using TaskForge.Notifications.Api.Contracts;
using static TaskForge.Notifications.Api.Services.Common.NotificationsApiCommonService;
using static TaskForge.Notifications.Api.Services.Mapping.NotificationsApiMappingService;

namespace TaskForge.Notifications.Api.Endpoints;

internal static partial class NotificationsApiEndpoints
{
    private static WebApplication MapServiceInfoEndpoints(WebApplication app)
    {
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
            status = "microservice boundary active"
        }));

        app.MapGet("/api/notifications/schema-owner", () => Results.Ok(new
        {
            database = "taskforge_notifications",
            ownedEntities = new[] { "Notification", "NotificationSubscription", "NotificationOutboxMessage" }
        }));

        return app;
    }
}
