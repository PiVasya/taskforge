using Microsoft.EntityFrameworkCore;
using TaskForge.Observability.Api.Data;

namespace TaskForge.Observability.Api.Endpoints;

internal static partial class ObservabilityApiEndpoints
{
    private sealed record AccountLifecycleMutationRequest(Guid OperationId, Guid SourceUserId, Guid? TargetUserId);

    private static WebApplication MapAccountLifecycleInternalEndpoints(WebApplication app)
    {
        app.MapPost("/api/internal/account-lifecycle/merge", async (
            AccountLifecycleMutationRequest request,
            ObservabilityDbContext db,
            CancellationToken ct) =>
        {
            if (!request.TargetUserId.HasValue || request.TargetUserId == request.SourceUserId)
                return Results.BadRequest(new { message = "Некорректная пара аккаунтов.", code = "INVALID_MERGE_USERS" });
            var rows = await db.PageViews.Where(x => x.UserId == request.SourceUserId).ToListAsync(ct);
            foreach (var row in rows) row.UserId = request.TargetUserId.Value;
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { request.OperationId, movedPageViews = rows.Count });
        });

        app.MapPost("/api/internal/account-lifecycle/delete", async (
            AccountLifecycleMutationRequest request,
            ObservabilityDbContext db,
            CancellationToken ct) =>
        {
            var rows = await db.PageViews.Where(x => x.UserId == request.SourceUserId).ToListAsync(ct);
            db.PageViews.RemoveRange(rows);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { request.OperationId, deletedPageViews = rows.Count });
        });

        return app;
    }
}
