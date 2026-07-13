using Microsoft.EntityFrameworkCore;
using TaskForge.Minecraft.Api.Contracts;
using TaskForge.Minecraft.Api.Data;
using TaskForge.Minecraft.Api.Domain;
using static TaskForge.Minecraft.Api.Services.Common.MinecraftApiCommonService;
using static TaskForge.Minecraft.Api.Services.Mapping.MinecraftApiMappingService;

namespace TaskForge.Minecraft.Api.Endpoints;

internal static partial class MinecraftApiEndpoints
{
    private static WebApplication MapAdminEndpoints(WebApplication app)
    {
        app.MapGet("/api/admin/minecraft-links", async (MinecraftDbContext db, IConfiguration cfg, IHttpClientFactory httpFactory, string? query, CancellationToken ct) =>
        {
            var links = await db.Links.AsNoTracking()
                .Where(x => x.Confirmed)
                .OrderByDescending(x => x.ConfirmedAtUtc ?? x.CreatedAt)
                .Take(1000)
                .ToListAsync(ct);

            var groupedRaw = links
                .Where(x => x.UserId.HasValue)
                .GroupBy(x => x.UserId!.Value)
                .Select(g => new
                {
                    UserId = g.Key,
                    Latest = g.Where(x => x.UnlinkedAtUtc == null).OrderByDescending(x => x.ConfirmedAtUtc ?? x.CreatedAt).FirstOrDefault()
                        ?? g.OrderByDescending(x => x.ConfirmedAtUtc ?? x.CreatedAt).First(),
                    LinkCount = g.Count()
                })
                .ToList();

            var ids = groupedRaw.Select(x => x.UserId).ToArray();
            var users = await LoadUserSummariesAsync(ids, cfg, httpFactory, ct);

            var rows = new List<object>();
            foreach (var item in groupedRaw)
            {
                users.TryGetValue(item.UserId, out var user);
                var balance = await BuildMinecraftRatingBalanceAsync(item.UserId, db, cfg, httpFactory, ct);
                var roles = (user?.FeatureRoles ?? user?.Roles ?? Array.Empty<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToArray();
                rows.Add(new
                {
                    id = item.Latest.Id,
                    userId = item.UserId,
                    login = user?.Login,
                    fullName = UserLabel(user),
                    displayName = UserLabel(user),
                    email = user?.Email ?? user?.MaskedEmail,
                    minecraftNick = item.Latest.PlayerName,
                    nick = item.Latest.PlayerName,
                    minecraftUuid = item.Latest.PlayerUuid,
                    uuid = item.Latest.PlayerUuid,
                    linkedAtUtc = item.Latest.ConfirmedAtUtc ?? item.Latest.CreatedAt,
                    unlinkedAtUtc = item.Latest.UnlinkedAtUtc,
                    linkCount = item.LinkCount,
                    totalScore = balance.baseRating,
                    baseRating = balance.baseRating,
                    minecraftBalance = balance.balance,
                    effectiveScore = balance.effectiveRating,
                    minecraftAdjustment = balance.adjustmentTotal,
                    totalSpent = balance.spentTotal,
                    minecraftSpent = balance.spentTotal,
                    minecraftRestored = balance.restoredTotal,
                    deathCoordinatesCost = balance.deathCoordinatesCost,
                    deathChestCost = balance.deathChestCost,
                    deathTeleportCost = balance.deathTeleportCost,
                    lastSpentAtUtc = await db.RatingTransactions.AsNoTracking().Where(x => x.UserId == item.UserId && x.Delta < 0).MaxAsync(x => (DateTimeOffset?)x.CreatedAtUtc, ct),
                    featureRoles = roles,
                    debuffed = false
                });
            }

            var search = (query ?? string.Empty).Trim().ToLowerInvariant();
            if (!string.IsNullOrWhiteSpace(search))
            {
                rows = rows.Where(x => string.Join(' ', x.GetType().GetProperties().Select(p => p.GetValue(x)?.ToString() ?? string.Empty)).ToLowerInvariant().Contains(search)).ToList();
            }

            return Microsoft.AspNetCore.Http.Results.Ok(rows);
        });

        app.MapGet("/api/admin/minecraft-links/users/{userId:guid}/rating", async (Guid userId, MinecraftDbContext db, IConfiguration cfg, IHttpClientFactory httpFactory, CancellationToken ct) =>
        {
            var balance = await BuildMinecraftRatingBalanceAsync(userId, db, cfg, httpFactory, ct);
            var active = await db.Links.AsNoTracking().Where(x => x.UserId == userId && x.Confirmed && x.UnlinkedAtUtc == null).OrderByDescending(x => x.ConfirmedAtUtc ?? x.CreatedAt).FirstOrDefaultAsync(ct);
            var linkCount = await db.Links.AsNoTracking().CountAsync(x => x.UserId == userId && x.Confirmed, ct);
            var rows = await db.RatingTransactions.AsNoTracking()
                .Where(x => x.UserId == userId)
                .OrderByDescending(x => x.CreatedAtUtc)
                .Take(80)
                .ToListAsync(ct);

            return Microsoft.AspNetCore.Http.Results.Ok(new
            {
                userId,
                linked = active != null,
                nick = active?.PlayerName,
                uuid = active?.PlayerUuid,
                linkCount,
                baseRating = balance.baseRating,
                minecraftBalance = balance.balance,
                balance = balance.balance,
                effectiveScore = balance.effectiveRating,
                minecraftAdjustment = balance.adjustmentTotal,
                minecraftSpent = balance.spentTotal,
                minecraftRestored = balance.restoredTotal,
                deathCoordinatesCost = balance.deathCoordinatesCost,
                deathChestCost = balance.deathChestCost,
                deathTeleportCost = balance.deathTeleportCost,
                transactions = rows.Select(ToRatingTransactionDto).ToArray()
            });
        });

        app.MapPost("/api/admin/minecraft-links/users/{userId:guid}/rating/restore", async (Guid userId, MinecraftRatingRestoreRequest request, HttpContext http, MinecraftDbContext db, IConfiguration cfg, IHttpClientFactory httpFactory, CancellationToken ct) =>
        {
            if (request.Amount <= 0 || request.Amount > 1000000) return Microsoft.AspNetCore.Http.Results.BadRequest(new { message = "Сумма восстановления должна быть больше 0." });
            var actor = UserId(http, cfg);
            var active = await db.Links.AsNoTracking().Where(x => x.UserId == userId && x.Confirmed && x.UnlinkedAtUtc == null).OrderByDescending(x => x.ConfirmedAtUtc ?? x.CreatedAt).FirstOrDefaultAsync(ct);
            db.RatingTransactions.Add(new MinecraftRatingTransaction
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                PlayerName = active?.PlayerName,
                PlayerUuid = active?.PlayerUuid,
                Delta = request.Amount,
                Kind = "admin-restore",
                Reason = string.IsNullOrWhiteSpace(request.Reason) ? "Восстановление Minecraft-баланса администратором" : request.Reason.Trim()[..Math.Min(500, request.Reason.Trim().Length)],
                RequestId = "admin-restore-" + Guid.NewGuid().ToString("N"),
                ActorUserId = actor,
                CreatedAtUtc = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync(ct);
            var balance = await BuildMinecraftRatingBalanceAsync(userId, db, cfg, httpFactory, ct);
            return Microsoft.AspNetCore.Http.Results.Ok(new { userId, restored = request.Amount, minecraftBalance = balance.balance, balance = balance.balance, effectiveScore = balance.effectiveRating, minecraftSpent = balance.spentTotal, minecraftRestored = balance.restoredTotal });
        });

        return app;
    }
}
