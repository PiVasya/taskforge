using Microsoft.EntityFrameworkCore;
using TaskForge.Minecraft.Api.Data;

namespace TaskForge.Minecraft.Api.Endpoints;

internal static partial class MinecraftApiEndpoints
{
    private sealed record AccountLifecycleMutationRequest(Guid OperationId, Guid SourceUserId, Guid? TargetUserId);

    private static WebApplication MapAccountLifecycleInternalEndpoints(WebApplication app)
    {
        app.MapPost("/api/internal/account-lifecycle/merge", async (
            AccountLifecycleMutationRequest request,
            MinecraftDbContext db,
            CancellationToken ct) =>
        {
            if (!request.TargetUserId.HasValue || request.TargetUserId == request.SourceUserId)
                return Results.BadRequest(new { message = "Некорректная пара аккаунтов.", code = "INVALID_MERGE_USERS" });
            var targetId = request.TargetUserId.Value;
            await using var transaction = await db.Database.BeginTransactionAsync(ct);

            var links = await db.Links.Where(x => x.UserId == request.SourceUserId).ToListAsync(ct);
            foreach (var row in links) row.UserId = targetId;
            var codes = await db.LinkCodes.Where(x => x.UserId == request.SourceUserId).ToListAsync(ct);
            db.LinkCodes.RemoveRange(codes);
            var chat = await db.ChatMessages.Where(x => x.UserId == request.SourceUserId).ToListAsync(ct);
            foreach (var row in chat) row.UserId = targetId;
            var transactions = await db.RatingTransactions
                .Where(x => x.UserId == request.SourceUserId || x.ActorUserId == request.SourceUserId)
                .ToListAsync(ct);
            foreach (var row in transactions)
            {
                if (row.UserId == request.SourceUserId) row.UserId = targetId;
                if (row.ActorUserId == request.SourceUserId) row.ActorUserId = targetId;
            }
            var deaths = await db.DeathRecoveries.Where(x => x.UserId == request.SourceUserId).ToListAsync(ct);
            foreach (var row in deaths) row.UserId = targetId;

            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return Results.Ok(new
            {
                request.OperationId,
                movedLinks = links.Count,
                removedPendingLinkCodes = codes.Count,
                movedChatMessages = chat.Count,
                movedRatingTransactions = transactions.Count,
                movedDeathRecoveries = deaths.Count,
            });
        });

        app.MapPost("/api/internal/account-lifecycle/delete", async (
            AccountLifecycleMutationRequest request,
            MinecraftDbContext db,
            CancellationToken ct) =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var links = await db.Links.Where(x => x.UserId == request.SourceUserId).ToListAsync(ct);
            var codes = await db.LinkCodes.Where(x => x.UserId == request.SourceUserId).ToListAsync(ct);
            var chat = await db.ChatMessages.Where(x => x.UserId == request.SourceUserId).ToListAsync(ct);
            var transactions = await db.RatingTransactions.Where(x => x.UserId == request.SourceUserId).ToListAsync(ct);
            var actorRows = await db.RatingTransactions.Where(x => x.ActorUserId == request.SourceUserId).ToListAsync(ct);
            var deaths = await db.DeathRecoveries.Where(x => x.UserId == request.SourceUserId).ToListAsync(ct);
            db.Links.RemoveRange(links);
            db.LinkCodes.RemoveRange(codes);
            db.RatingTransactions.RemoveRange(transactions);
            foreach (var row in actorRows) row.ActorUserId = null;
            foreach (var row in chat) row.UserId = null;
            foreach (var row in deaths) row.UserId = null;
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return Results.Ok(new
            {
                request.OperationId,
                deletedLinks = links.Count,
                deletedPendingLinkCodes = codes.Count,
                anonymizedChatMessages = chat.Count,
                deletedRatingTransactions = transactions.Count,
                anonymizedDeathRecoveries = deaths.Count,
            });
        });

        return app;
    }
}
