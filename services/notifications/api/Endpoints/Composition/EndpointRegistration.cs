using Microsoft.EntityFrameworkCore;
using TaskForge.Notifications.Api.Data;
using TaskForge.Notifications.Api.Domain;

using TaskForge.Notifications.Api.Contracts;
using static TaskForge.Notifications.Api.Services.Common.NotificationsApiCommonService;
using static TaskForge.Notifications.Api.Services.Mapping.NotificationsApiMappingService;

namespace TaskForge.Notifications.Api.Endpoints;

internal static partial class NotificationsApiEndpoints
{
    internal static WebApplication MapNotificationsApiEndpoints(this WebApplication app)
    {
        MapServiceInfoEndpoints(app);
        MapNotificationsEndpoints(app);
        MapInternalEndpoints(app);

        return app;
    }
}
