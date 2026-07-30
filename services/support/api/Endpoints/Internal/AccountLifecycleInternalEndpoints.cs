using Microsoft.EntityFrameworkCore;
using TaskForge.Support.Api.Data;

namespace TaskForge.Support.Api.Endpoints;

internal static partial class SupportApiEndpoints
{
    private sealed record AccountLifecycleMutationRequest(Guid OperationId, Guid SourceUserId, Guid? TargetUserId);

    private static WebApplication MapAccountLifecycleInternalEndpoints(WebApplication app)
    {
        app.MapPost("/api/internal/account-lifecycle/merge", async (
            AccountLifecycleMutationRequest request,
            SupportDbContext db,
            CancellationToken ct) =>
        {
            if (!request.TargetUserId.HasValue || request.TargetUserId == request.SourceUserId)
                return Results.BadRequest(new { message = "Некорректная пара аккаунтов.", code = "INVALID_MERGE_USERS" });
            var targetId = request.TargetUserId.Value;
            var tickets = await db.Tickets.Where(x => x.UserId == request.SourceUserId).ToListAsync(ct);
            foreach (var row in tickets) row.UserId = targetId;
            var messages = await db.Messages.Where(x => x.UserId == request.SourceUserId).ToListAsync(ct);
            foreach (var row in messages) row.UserId = targetId;
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { request.OperationId, movedTickets = tickets.Count, movedMessages = messages.Count });
        });

        app.MapPost("/api/internal/account-lifecycle/delete", async (
            AccountLifecycleMutationRequest request,
            SupportDbContext db,
            CancellationToken ct) =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var tickets = await db.Tickets.Where(x => x.UserId == request.SourceUserId).ToListAsync(ct);
            var ticketIds = tickets.Select(x => x.Id).ToArray();
            var messages = await db.Messages
                .Where(x => x.UserId == request.SourceUserId || ticketIds.Contains(x.TicketId))
                .ToListAsync(ct);
            db.Messages.RemoveRange(messages);
            db.Tickets.RemoveRange(tickets);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return Results.Ok(new { request.OperationId, deletedTickets = tickets.Count, deletedMessages = messages.Count });
        });

        return app;
    }
}
