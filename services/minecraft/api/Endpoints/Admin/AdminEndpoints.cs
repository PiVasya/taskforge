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
                    ActiveLinks = g.Where(x => x.UnlinkedAtUtc == null)
                        .OrderByDescending(x => x.ConfirmedAtUtc ?? x.CreatedAt)
                        .ToArray(),
                    Latest = g.Where(x => x.UnlinkedAtUtc == null).OrderByDescending(x => x.ConfirmedAtUtc ?? x.CreatedAt).FirstOrDefault()
                        ?? g.OrderByDescending(x => x.ConfirmedAtUtc ?? x.CreatedAt).First(),
                    ActiveLinkCount = g.Count(x => x.UnlinkedAtUtc == null),
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
                    activeLinkCount = item.ActiveLinkCount,
                    linkCount = item.LinkCount,
                    activeLinks = item.ActiveLinks.Select(x => new
                    {
                        id = x.Id,
                        nick = x.PlayerName,
                        uuid = x.PlayerUuid,
                        linkedAtUtc = x.ConfirmedAtUtc ?? x.CreatedAt
                    }).ToArray(),
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
            var activeLinks = await db.Links.AsNoTracking()
                .Where(x => x.UserId == userId && x.Confirmed && x.UnlinkedAtUtc == null)
                .OrderByDescending(x => x.ConfirmedAtUtc ?? x.CreatedAt)
                .ToListAsync(ct);
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
                activeLinkCount = activeLinks.Count,
                linkCount,
                activeLinks = activeLinks.Select(x => new
                {
                    id = x.Id,
                    nick = x.PlayerName,
                    uuid = x.PlayerUuid,
                    linkedAtUtc = x.ConfirmedAtUtc ?? x.CreatedAt
                }).ToArray(),
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


        app.MapDelete("/api/admin/minecraft-links/{linkId:guid}", async (
            Guid linkId,
            HttpContext http,
            MinecraftDbContext db,
            IConfiguration cfg,
            IHttpClientFactory httpFactory,
            ILogger<Program> logger,
            CancellationToken ct) =>
        {
            var actor = UserId(http, cfg);
            if (!actor.HasValue) return Unauthorized();

            var link = await db.Links.FirstOrDefaultAsync(x => x.Id == linkId && x.Confirmed && x.UnlinkedAtUtc == null, ct);
            if (link == null) return Microsoft.AspNetCore.Http.Results.NotFound(new { message = "Активная Minecraft-привязка не найдена.", code = "MINECRAFT_LINK_NOT_FOUND" });
            if (!link.UserId.HasValue) return Microsoft.AspNetCore.Http.Results.Conflict(new { message = "У привязки отсутствует владелец.", code = "MINECRAFT_LINK_OWNER_MISSING" });

            var userId = link.UserId.Value;
            link.UnlinkedAtUtc = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);

            var activeLinksRemaining = await db.Links.AsNoTracking().CountAsync(x => x.UserId == userId && x.Confirmed && x.UnlinkedAtUtc == null, ct);
            if (activeLinksRemaining == 0)
                await RemoveMinecraftRoleAsync(userId, cfg, httpFactory, logger, ct);

            logger.LogInformation(
                "Admin removed Minecraft link: actor={ActorUserId} user={UserId} linkId={LinkId} nick={Nick} uuid={Uuid} activeLinksRemaining={ActiveLinksRemaining}; rating ledger preserved",
                actor.Value,
                userId,
                link.Id,
                link.PlayerName,
                link.PlayerUuid,
                activeLinksRemaining);

            return Microsoft.AspNetCore.Http.Results.Ok(new
            {
                userId,
                linkId,
                unlinked = true,
                activeLinksRemaining
            });
        });

        app.MapDelete("/api/admin/minecraft-links/users/{userId:guid}", async (
            Guid userId,
            HttpContext http,
            MinecraftDbContext db,
            IConfiguration cfg,
            IHttpClientFactory httpFactory,
            ILogger<Program> logger,
            CancellationToken ct) =>
        {
            var actor = UserId(http, cfg);
            if (!actor.HasValue) return Unauthorized();

            var now = DateTimeOffset.UtcNow;
            var links = await db.Links
                .Where(x => x.UserId == userId && x.Confirmed && x.UnlinkedAtUtc == null)
                .ToListAsync(ct);
            foreach (var link in links) link.UnlinkedAtUtc = now;
            await db.SaveChangesAsync(ct);
            if (links.Count > 0)
                await RemoveMinecraftRoleAsync(userId, cfg, httpFactory, logger, ct);

            logger.LogInformation(
                "Admin removed all Minecraft links: actor={ActorUserId} user={UserId} removed={Removed}; rating ledger preserved",
                actor.Value,
                userId,
                links.Count);

            return Microsoft.AspNetCore.Http.Results.Ok(new
            {
                userId,
                unlinked = true,
                removed = links.Count,
                activeLinksRemaining = 0
            });
        });

        return app;
    }
}
