using Microsoft.EntityFrameworkCore;
using TaskForge.Notifications.Api.Data;

namespace TaskForge.Notifications.Api.Endpoints;

internal static partial class NotificationsApiEndpoints
{
    private sealed record AccountLifecycleMutationRequest(Guid OperationId, Guid SourceUserId, Guid? TargetUserId);

    private static WebApplication MapAccountLifecycleInternalEndpoints(WebApplication app)
    {
        app.MapPost("/api/internal/account-lifecycle/merge", async (
            AccountLifecycleMutationRequest request,
            NotificationsDbContext db,
            CancellationToken ct) =>
        {
            if (!request.TargetUserId.HasValue || request.TargetUserId == request.SourceUserId)
                return Results.BadRequest(new { message = "Некорректная пара аккаунтов.", code = "INVALID_MERGE_USERS" });
            var rows = await db.Notifications.Where(x => x.UserId == request.SourceUserId).ToListAsync(ct);
            foreach (var row in rows) row.UserId = request.TargetUserId.Value;
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { request.OperationId, movedNotifications = rows.Count });
        });

        app.MapPost("/api/internal/account-lifecycle/delete", async (
            AccountLifecycleMutationRequest request,
            NotificationsDbContext db,
            CancellationToken ct) =>
        {
            var rows = await db.Notifications.Where(x => x.UserId == request.SourceUserId).ToListAsync(ct);
            db.Notifications.RemoveRange(rows);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { request.OperationId, deletedNotifications = rows.Count });
        });

        return app;
    }
}
