using Microsoft.EntityFrameworkCore;
using TaskForge.Notifications.Api.Data;
using TaskForge.Notifications.Api.Domain;

using TaskForge.Notifications.Api.Contracts;
using static TaskForge.Notifications.Api.Services.Common.NotificationsApiCommonService;
using static TaskForge.Notifications.Api.Services.Mapping.NotificationsApiMappingService;

namespace TaskForge.Notifications.Api.Endpoints;

internal static partial class NotificationsApiEndpoints
{
    private static WebApplication MapNotificationsEndpoints(WebApplication app)
    {
        app.MapGet("/api/notifications", async (HttpContext http, IConfiguration cfg, NotificationsDbContext db, Guid? userId, bool unreadOnly = false, int take = 100) =>
        {
            var currentUserId = TaskForgeRequestSecurity.UserId(http, cfg);
            if (!currentUserId.HasValue) return Microsoft.AspNetCore.Http.Results.Unauthorized();

            var isAdmin = IsAdmin(http, cfg);
            var effectiveUserId = isAdmin && userId.HasValue ? userId.Value : currentUserId.Value;

            var query = db.Notifications.AsNoTracking().Where(x => x.UserId == effectiveUserId);
            if (unreadOnly) query = query.Where(x => !x.IsRead);

            var rows = await query
                .OrderByDescending(x => x.CreatedAt)
                .Take(System.Math.Clamp(take, 1, 500))
                .ToListAsync();

            return Microsoft.AspNetCore.Http.Results.Ok(rows.Select(ToDto).ToList());
        });

        app.MapPost("/api/notifications", async (NotificationRequest request, HttpContext http, IConfiguration cfg, NotificationsDbContext db) =>
        {
            if (!IsAdmin(http, cfg)) return Forbidden("Создавать уведомления может только администратор или внутренний сервис.");
            return await CreateNotification(request, db);
        });

        app.MapPost("/api/notifications/{id:guid}/read", async (Guid id, HttpContext http, IConfiguration cfg, NotificationsDbContext db) =>
        {
            var currentUserId = TaskForgeRequestSecurity.UserId(http, cfg);
            if (!currentUserId.HasValue) return Microsoft.AspNetCore.Http.Results.Unauthorized();

            var isAdmin = IsAdmin(http, cfg);
            var item = await db.Notifications.FirstOrDefaultAsync(x => x.Id == id && (isAdmin || x.UserId == currentUserId.Value));
            if (item == null) return Microsoft.AspNetCore.Http.Results.NotFound();

            item.IsRead = true;
            await db.SaveChangesAsync();
            return Microsoft.AspNetCore.Http.Results.Ok(ToDto(item));
        });

        return app;
    }
}
