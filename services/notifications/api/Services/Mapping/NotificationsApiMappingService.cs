using Microsoft.EntityFrameworkCore;
using TaskForge.Notifications.Api.Data;
using TaskForge.Notifications.Api.Domain;

using TaskForge.Notifications.Api.Contracts;
using static TaskForge.Notifications.Api.Services.Common.NotificationsApiCommonService;

namespace TaskForge.Notifications.Api.Services.Mapping;

internal static class NotificationsApiMappingService
{
    internal static async Task<IResult> CreateNotification(NotificationRequest request, NotificationsDbContext db)
    {
        if (!request.UserId.HasValue || request.UserId.Value == Guid.Empty)
        {
            return Microsoft.AspNetCore.Http.Results.BadRequest(new { message = "UserId is required.", code = "USER_ID_REQUIRED" });
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
        return Microsoft.AspNetCore.Http.Results.Ok(ToDto(item));
    }

    internal static object ToDto(NotificationItem x) => new { x.Id, x.UserId, x.Type, x.Title, x.Message, x.IsRead, x.CreatedAt };

}
