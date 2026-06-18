using Microsoft.EntityFrameworkCore;
using TaskForge.Notifications.Api.Data;
using TaskForge.Notifications.Api.Domain;

using TaskForge.Notifications.Api.Contracts;
using static TaskForge.Notifications.Api.Services.Common.NotificationsApiCommonService;
using static TaskForge.Notifications.Api.Services.Mapping.NotificationsApiMappingService;

namespace TaskForge.Notifications.Api.Endpoints;

internal static partial class NotificationsApiEndpoints
{
    private static WebApplication MapInternalEndpoints(WebApplication app)
    {
        app.MapPost("/api/internal/notifications", async (NotificationRequest request, NotificationsDbContext db) => await CreateNotification(request, db));

        return app;
    }
}
