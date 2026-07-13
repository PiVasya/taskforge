using Microsoft.EntityFrameworkCore;
using TaskForge.Minecraft.Api.Contracts;
using TaskForge.Minecraft.Api.Data;
using TaskForge.Minecraft.Api.Domain;
using static TaskForge.Minecraft.Api.Services.Common.MinecraftApiCommonService;

namespace TaskForge.Minecraft.Api.Endpoints;

internal static partial class MinecraftApiEndpoints
{
    private static WebApplication MapLinksEndpoints(WebApplication app)
    {
        app.MapGet("/api/integrations/minecraft/status", async (HttpContext http, IConfiguration cfg, MinecraftDbContext db, IHttpClientFactory httpFactory, CancellationToken ct) =>
        {
            var uid = UserId(http, cfg);
            if (uid == null) return Unauthorized();
            var status = await BuildStatusAsync(uid.Value, db, cfg, httpFactory, ct);
            return Microsoft.AspNetCore.Http.Results.Ok(status);
        });

        app.MapPost("/api/integrations/minecraft/request", async (MinecraftLinkRequest req, HttpContext http, IConfiguration cfg, MinecraftDbContext db, IHttpClientFactory httpFactory, ILogger<Program> logger, CancellationToken ct) =>
        {
            var uid = UserId(http, cfg);
            if (uid == null) return Unauthorized();
            var nick = NormalizeNick(req.Nick);
            if (!IsValidNick(nick)) return Microsoft.AspNetCore.Http.Results.BadRequest(new { message = "Неверный ник. Разрешены A-Z, 0-9, _ (3..16 символов)." });

            var confirmedCount = await db.Links.AsNoTracking().CountAsync(x => x.UserId == uid.Value && x.Confirmed, ct);
            if (confirmedCount >= 2) return Microsoft.AspNetCore.Http.Results.Json(new { message = "Лимит привязок Minecraft исчерпан (2/2)." }, statusCode: StatusCodes.Status403Forbidden);

            var activeLink = await db.Links.AsNoTracking().FirstOrDefaultAsync(x => x.UserId == uid.Value && x.Confirmed && x.UnlinkedAtUtc == null, ct);
            if (activeLink != null) return Microsoft.AspNetCore.Http.Results.Conflict(new { message = "Minecraft уже привязан. Сначала отвяжи." });

            var now = DateTimeOffset.UtcNow;
            var oldCodes = await db.LinkCodes.Where(x => x.UserId == uid.Value && (x.UsedAtUtc != null || x.ExpiresAtUtc < now)).ToListAsync(ct);
            if (oldCodes.Count > 0) db.LinkCodes.RemoveRange(oldCodes);

            var code = GenerateCode();
            var (salt, hash) = HashCode(code);
            var expires = now.AddMinutes(10);
            db.LinkCodes.Add(new MinecraftLinkCode
            {
                Id = Guid.NewGuid(),
                UserId = uid.Value,
                Nick = nick,
                Salt = salt,
                CodeHash = hash,
                ExpiresAtUtc = expires,
                CreatedAtUtc = now
            });
            await db.SaveChangesAsync(ct);

            logger.LogInformation("Minecraft link code generated: user={UserId} nick={Nick} code={Code} exp={Exp}", uid, nick, code, expires);
            var deliveryResult = await SendLinkCodeAsync(nick, code, cfg, httpFactory, logger, ct);
            var attempted = !string.IsNullOrWhiteSpace(cfg["MINECRAFT_WEBHOOK_BASE_URL"]) || !string.IsNullOrWhiteSpace(cfg["MINECRAFT_SERVER_URL"]);
            var status = await BuildStatusAsync(uid.Value, db, cfg, httpFactory, ct);
            return Microsoft.AspNetCore.Http.Results.Ok(new
            {
                code,
                expiresAtUtc = expires,
                expiresInSeconds = (int)Math.Max(0, (expires - DateTimeOffset.UtcNow).TotalSeconds),
                status,
                delivery = new { attempted, delivered = deliveryResult.Ok, message = deliveryResult.Message }
            });
        });

        app.MapPost("/api/integrations/minecraft/confirm", async (MinecraftConfirmRequest req, HttpContext http, IConfiguration cfg, MinecraftDbContext db, IHttpClientFactory httpFactory, ILogger<Program> logger, CancellationToken ct) =>
        {
            var uid = UserId(http, cfg);
            if (uid == null) return Unauthorized();
            var code = (req.Code ?? string.Empty).Trim().ToUpperInvariant();
            if (code.Length < 4 || code.Length > 32) return Microsoft.AspNetCore.Http.Results.BadRequest(new { message = "Неверный код." });

            var confirmedCount = await db.Links.AsNoTracking().CountAsync(x => x.UserId == uid.Value && x.Confirmed, ct);
            if (confirmedCount >= 2) return Microsoft.AspNetCore.Http.Results.Json(new { message = "Лимит привязок Minecraft исчерпан (2/2)." }, statusCode: StatusCodes.Status403Forbidden);
            var activeLink = await db.Links.AsNoTracking().FirstOrDefaultAsync(x => x.UserId == uid.Value && x.Confirmed && x.UnlinkedAtUtc == null, ct);
            if (activeLink != null) return Microsoft.AspNetCore.Http.Results.Conflict(new { message = "Minecraft уже привязан. Сначала отвяжи." });

            var now = DateTimeOffset.UtcNow;
            var candidates = await db.LinkCodes.Where(x => x.UserId == uid.Value && x.UsedAtUtc == null && x.ExpiresAtUtc >= now).OrderByDescending(x => x.CreatedAtUtc).Take(50).ToListAsync(ct);
            MinecraftLinkCode? match = null;
            foreach (var item in candidates)
            {
                if (HashCodeWithSalt(code, item.Salt).SequenceEqual(item.CodeHash))
                {
                    match = item;
                    break;
                }
            }
            if (match == null) return Microsoft.AspNetCore.Http.Results.NotFound(new { message = "Код не найден или устарел. Сгенерируй новый." });

            var confirmedUuid = string.IsNullOrWhiteSpace(req.PlayerUuid)
                ? null
                : req.PlayerUuid.Trim().ToLowerInvariant();
            if (confirmedUuid is not null)
            {
                var uuidOwner = await db.Links.AsNoTracking().FirstOrDefaultAsync(x => x.Confirmed
                    && x.UnlinkedAtUtc == null
                    && x.PlayerUuid != null
                    && x.PlayerUuid.ToLower() == confirmedUuid, ct);
                if (uuidOwner is not null && uuidOwner.UserId != uid.Value)
                    return Microsoft.AspNetCore.Http.Results.Conflict(new { message = "Этот Minecraft UUID уже привязан к другой учётной записи." });
            }

            match.UsedAtUtc = now;
            db.Links.Add(new MinecraftLink
            {
                Id = Guid.NewGuid(),
                UserId = uid.Value,
                PlayerName = match.Nick,
                PlayerUuid = confirmedUuid,
                Code = "LINK-" + Guid.NewGuid().ToString("N"),
                Confirmed = true,
                CreatedAt = now,
                ConfirmedAtUtc = now
            });
            await db.SaveChangesAsync(ct);
            await AssignMinecraftRoleAsync(uid.Value, cfg, httpFactory, logger, ct);
            logger.LogInformation("Minecraft linked: user={UserId} nick={Nick}", uid, match.Nick);
            var status = await BuildStatusAsync(uid.Value, db, cfg, httpFactory, ct);
            return Microsoft.AspNetCore.Http.Results.Ok(status);
        });

        app.MapDelete("/api/integrations/minecraft/unlink", async (HttpContext http, IConfiguration cfg, MinecraftDbContext db, IHttpClientFactory httpFactory, ILogger<Program> logger, CancellationToken ct) =>
        {
            var uid = UserId(http, cfg);
            if (uid == null) return Unauthorized();
            var now = DateTimeOffset.UtcNow;
            var links = await db.Links.Where(x => x.UserId == uid.Value && x.Confirmed && x.UnlinkedAtUtc == null).ToListAsync(ct);
            foreach (var link in links) link.UnlinkedAtUtc = now;
            await db.SaveChangesAsync(ct);
            await RemoveMinecraftRoleAsync(uid.Value, cfg, httpFactory, logger, ct);
            return Microsoft.AspNetCore.Http.Results.Ok(new { linked = false });
        });

        app.MapGet("/api/integrations/minecraft/economy", (IConfiguration cfg) =>
            Microsoft.AspNetCore.Http.Results.Ok(new
            {
                deathCoordinatesCost = DeathCoordinatesCost(cfg),
                deathChestCost = DeathChestCost(cfg),
                deathTeleportCost = DeathTeleportCost(cfg),
                deathChestAndTeleportCost = DeathChestCost(cfg) + DeathTeleportCost(cfg)
            }));

        app.MapPost("/api/integrations/minecraft/events/join", async (MinecraftJoinEventRequest request, HttpContext http, IConfiguration cfg, MinecraftDbContext db, IHttpClientFactory httpFactory, ILogger<Program> logger, CancellationToken ct) =>
        {
            if (!IsPluginAuthorized(http, cfg)) return Microsoft.AspNetCore.Http.Results.Unauthorized();
            var nick = NormalizeNick(request.Nick);
            if (!IsValidNick(nick)) return Microsoft.AspNetCore.Http.Results.BadRequest(new { message = "bad nick" });
            var uuid = (request.Uuid ?? string.Empty).Trim();
            logger.LogInformation("Minecraft join status request: nick={Nick} uuid={Uuid} remote={Remote}", nick, uuid, http.Connection.RemoteIpAddress);
            var active = await FindActiveLinkAsync(db, nick, uuid, ct);
            if (active == null || active.UserId == null)
            {
                logger.LogInformation("Minecraft join resolved as unlinked: nick={Nick} uuid={Uuid}", nick, uuid);
                return Microsoft.AspNetCore.Http.Results.Ok(new
                {
                    linked = false,
                    nick,
                    uuid = string.IsNullOrWhiteSpace(uuid) ? null : uuid,
                    linkCount = 0,
                    score = 0,
                    baseRating = 0,
                    minecraftBalance = 0,
                    balance = 0,
                    minecraftSpent = 0,
                    minecraftRestored = 0,
                    minecraftAdjustment = 0,
                    deathCoordinatesCost = DeathCoordinatesCost(cfg),
                    deathChestCost = DeathChestCost(cfg),
                    deathTeleportCost = DeathTeleportCost(cfg),
                    effectiveScore = 0,
                    debuffed = false
                });
            }
            var linkChanged = false;
            if (!string.IsNullOrWhiteSpace(uuid) && string.IsNullOrWhiteSpace(active.PlayerUuid))
            {
                active.PlayerUuid = uuid.ToLowerInvariant();
                linkChanged = true;
            }
            if (!string.Equals(active.PlayerName, nick, StringComparison.Ordinal))
            {
                active.PlayerName = nick;
                linkChanged = true;
            }
            if (linkChanged)
            {
                await db.SaveChangesAsync(ct);
                logger.LogInformation("Minecraft active link updated from join: linkId={LinkId} userId={UserId} nick={Nick} uuid={Uuid}", active.Id, active.UserId, active.PlayerName, active.PlayerUuid);
            }
            var dto = await BuildPlayerStatusAsync(active.UserId.Value, db, cfg, httpFactory, ct);
            logger.LogInformation("Minecraft join resolved as linked: nick={Nick} uuid={Uuid} user={UserId} balance={Balance} costs={CoordinatesCost}/{ChestCost}/{TeleportCost}", nick, uuid, active.UserId, dto.minecraftBalance, dto.deathCoordinatesCost, dto.deathChestCost, dto.deathTeleportCost);
            return Microsoft.AspNetCore.Http.Results.Ok(dto);
        });

        app.MapGet("/api/integrations/minecraft/player-status", async (string? nick, string? uuid, HttpContext http, IConfiguration cfg, MinecraftDbContext db, IHttpClientFactory httpFactory, ILogger<Program> logger, CancellationToken ct) =>
        {
            if (!IsPluginAuthorized(http, cfg))
            {
                logger.LogWarning("Minecraft player-status unauthorized: nick={Nick} uuid={Uuid} remote={Remote}", nick, uuid, http.Connection.RemoteIpAddress);
                return Microsoft.AspNetCore.Http.Results.Unauthorized();
            }
            var normalizedNick = NormalizeNick(nick);
            var normalizedUuid = (uuid ?? string.Empty).Trim();
            logger.LogInformation("Minecraft player-status start: nick={Nick} uuid={Uuid}", normalizedNick, normalizedUuid);
            var active = await FindActiveLinkAsync(db, normalizedNick, normalizedUuid, ct);
            if (active == null || active.UserId == null)
            {
                logger.LogInformation("Minecraft player-status result: linked=false nick={Nick} uuid={Uuid}", normalizedNick, normalizedUuid);
                return Microsoft.AspNetCore.Http.Results.Ok(new { linked = false, nick = normalizedNick, uuid = normalizedUuid, linkCount = 0, score = 0, baseRating = 0, minecraftBalance = 0, balance = 0, minecraftSpent = 0, minecraftRestored = 0, minecraftAdjustment = 0, deathCoordinatesCost = DeathCoordinatesCost(cfg), deathChestCost = DeathChestCost(cfg), deathTeleportCost = DeathTeleportCost(cfg), effectiveScore = 0, debuffed = false });
            }
            var dto = await BuildPlayerStatusAsync(active.UserId.Value, db, cfg, httpFactory, ct);
            logger.LogInformation("Minecraft player-status result: linked=true nick={Nick} uuid={Uuid} userId={UserId} balance={Balance}", normalizedNick, normalizedUuid, active.UserId, dto.minecraftBalance);
            return Microsoft.AspNetCore.Http.Results.Ok(dto);
        });


        return app;
    }

    private static async Task<MinecraftLink?> FindActiveLinkAsync(MinecraftDbContext db, string? nick, string? uuid, CancellationToken ct)
    {
        var normalizedUuid = (uuid ?? string.Empty).Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(normalizedUuid))
        {
            var byUuid = await db.Links.FirstOrDefaultAsync(x => x.Confirmed
                && x.UnlinkedAtUtc == null
                && x.PlayerUuid != null
                && x.PlayerUuid.ToLower() == normalizedUuid, ct);
            if (byUuid != null) return byUuid;
        }

        // Nick fallback exists only for legacy links that have never stored a UUID.
        // A renamed/recycled nickname must never reassign a link that already belongs to another UUID.
        if (!string.IsNullOrWhiteSpace(nick))
        {
            var lower = nick.ToLowerInvariant();
            return await db.Links.FirstOrDefaultAsync(x => x.Confirmed
                && x.UnlinkedAtUtc == null
                && (x.PlayerUuid == null || x.PlayerUuid == string.Empty)
                && x.PlayerName != null
                && x.PlayerName.ToLower() == lower, ct);
        }
        return null;
    }

    private static async Task<object> BuildStatusAsync(Guid userId, MinecraftDbContext db, IConfiguration cfg, IHttpClientFactory httpFactory, CancellationToken ct)
    {
        var active = await db.Links.AsNoTracking().Where(x => x.UserId == userId && x.Confirmed && x.UnlinkedAtUtc == null).OrderByDescending(x => x.ConfirmedAtUtc ?? x.CreatedAt).FirstOrDefaultAsync(ct);
        var linkCount = await db.Links.AsNoTracking().CountAsync(x => x.UserId == userId && x.Confirmed, ct);
        if (active == null)
        {
            return new { linked = false, nick = (string?)null, uuid = (string?)null, linkCount, score = 0, baseRating = 0, minecraftBalance = 0, balance = 0, minecraftSpent = 0, minecraftRestored = 0, minecraftAdjustment = 0, deathCoordinatesCost = DeathCoordinatesCost(cfg), deathChestCost = DeathChestCost(cfg), deathTeleportCost = DeathTeleportCost(cfg), effectiveScore = 0, debuffed = false };
        }
        return await BuildPlayerStatusAsync(userId, db, cfg, httpFactory, ct);
    }

    private static async Task<MinecraftPlayerStatusDto> BuildPlayerStatusAsync(Guid userId, MinecraftDbContext db, IConfiguration cfg, IHttpClientFactory httpFactory, CancellationToken ct)
    {
        var active = await db.Links.AsNoTracking().Where(x => x.UserId == userId && x.Confirmed && x.UnlinkedAtUtc == null).OrderByDescending(x => x.ConfirmedAtUtc ?? x.CreatedAt).FirstOrDefaultAsync(ct);
        var linkCount = await db.Links.AsNoTracking().CountAsync(x => x.UserId == userId && x.Confirmed, ct);
        var balance = await BuildMinecraftRatingBalanceAsync(userId, db, cfg, httpFactory, ct);
        return new MinecraftPlayerStatusDto(
            active != null,
            active?.PlayerName,
            active?.PlayerUuid,
            active?.PlayerName,
            active?.PlayerUuid,
            active?.PlayerName,
            active?.PlayerUuid,
            active?.ConfirmedAtUtc ?? active?.CreatedAt,
            linkCount,
            balance.baseRating,
            balance.baseRating,
            balance.balance,
            balance.balance,
            balance.adjustmentTotal,
            balance.spentTotal,
            balance.restoredTotal,
            balance.deathCoordinatesCost,
            balance.deathChestCost,
            balance.deathTeleportCost,
            balance.effectiveRating,
            false);
    }

    private sealed record MinecraftPlayerStatusDto(
        bool linked,
        string? nick,
        string? uuid,
        string? minecraftNick,
        string? minecraftUuid,
        string? playerName,
        string? playerUuid,
        DateTimeOffset? linkedAtUtc,
        int linkCount,
        int score,
        int baseRating,
        int minecraftBalance,
        int balance,
        int minecraftAdjustment,
        int minecraftSpent,
        int minecraftRestored,
        int deathCoordinatesCost,
        int deathChestCost,
        int deathTeleportCost,
        int effectiveScore,
        bool debuffed);
}
