using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TaskForge.Minecraft.Api.Contracts;
using TaskForge.Minecraft.Api.Data;
using TaskForge.Minecraft.Api.Domain;
using static TaskForge.Minecraft.Api.Services.Common.MinecraftApiCommonService;
using static TaskForge.Minecraft.Api.Services.Serialization.MinecraftApiSerializationService;

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

            match.UsedAtUtc = now;
            db.Links.Add(new MinecraftLink
            {
                Id = Guid.NewGuid(),
                UserId = uid.Value,
                PlayerName = match.Nick,
                PlayerUuid = string.IsNullOrWhiteSpace(req.PlayerUuid) ? null : req.PlayerUuid.Trim(),
                Code = "LINK-" + Guid.NewGuid().ToString("N"),
                Confirmed = true,
                CreatedAt = now,
                ConfirmedAtUtc = now
            });
            await db.SaveChangesAsync(ct);
            await AssignMinecraftRoleAsync(uid.Value, cfg, httpFactory, ct);
            logger.LogInformation("Minecraft linked: user={UserId} nick={Nick}", uid, match.Nick);
            var status = await BuildStatusAsync(uid.Value, db, cfg, httpFactory, ct);
            return Microsoft.AspNetCore.Http.Results.Ok(status);
        });

        app.MapDelete("/api/integrations/minecraft/unlink", async (HttpContext http, IConfiguration cfg, MinecraftDbContext db, IHttpClientFactory httpFactory, CancellationToken ct) =>
        {
            var uid = UserId(http, cfg);
            if (uid == null) return Unauthorized();
            var now = DateTimeOffset.UtcNow;
            var links = await db.Links.Where(x => x.UserId == uid.Value && x.Confirmed && x.UnlinkedAtUtc == null).ToListAsync(ct);
            foreach (var link in links) link.UnlinkedAtUtc = now;
            await db.SaveChangesAsync(ct);
            await RemoveMinecraftRoleAsync(uid.Value, cfg, httpFactory, ct);
            return Microsoft.AspNetCore.Http.Results.Ok(new { linked = false });
        });

        app.MapGet("/api/integrations/minecraft/economy", (IConfiguration cfg) =>
            Microsoft.AspNetCore.Http.Results.Ok(new
            {
                weeklyPenalty = 0,
                deathTeleportCost = DeathTeleportCost(cfg)
            }));

        app.MapPost("/api/integrations/minecraft/events/join", async (MinecraftJoinEventRequest request, HttpContext http, IConfiguration cfg, MinecraftDbContext db, IHttpClientFactory httpFactory, ILogger<Program> logger, CancellationToken ct) =>
        {
            if (!IsPluginAuthorized(http, cfg)) return Microsoft.AspNetCore.Http.Results.Unauthorized();
            var nick = NormalizeNick(request.Nick);
            if (!IsValidNick(nick)) return Microsoft.AspNetCore.Http.Results.BadRequest(new { message = "bad nick" });
            var uuid = (request.Uuid ?? string.Empty).Trim();
            var active = await FindActiveLinkAsync(db, nick, uuid, ct);
            if (active == null || active.UserId == null)
            {
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
                    deathTeleportCost = DeathTeleportCost(cfg),
                    weeklyPenaltyCurrent = 0,
                    penaltyTotal = 0,
                    effectiveScore = 0,
                    chargedThisWeek = false,
                    debuffed = false
                });
            }
            if (!string.IsNullOrWhiteSpace(uuid) && active.PlayerUuid != uuid)
            {
                active.PlayerUuid = uuid;
                await db.SaveChangesAsync(ct);
            }
            var dto = await BuildPlayerStatusAsync(active.UserId.Value, db, cfg, httpFactory, false, ct);
            logger.LogInformation("MC join: nick={Nick} user={UserId} balance={Balance}", nick, active.UserId, dto.minecraftBalance);
            return Microsoft.AspNetCore.Http.Results.Ok(dto);
        });

        app.MapGet("/api/integrations/minecraft/player-status", async (string? nick, string? uuid, HttpContext http, IConfiguration cfg, MinecraftDbContext db, IHttpClientFactory httpFactory, CancellationToken ct) =>
        {
            if (!IsPluginAuthorized(http, cfg)) return Microsoft.AspNetCore.Http.Results.Unauthorized();
            var active = await FindActiveLinkAsync(db, NormalizeNick(nick), (uuid ?? string.Empty).Trim(), ct);
            if (active == null || active.UserId == null)
            {
                return Microsoft.AspNetCore.Http.Results.Ok(new { linked = false, nick, uuid, linkCount = 0, score = 0, baseRating = 0, minecraftBalance = 0, balance = 0, minecraftSpent = 0, minecraftRestored = 0, minecraftAdjustment = 0, deathTeleportCost = DeathTeleportCost(cfg), weeklyPenaltyCurrent = 0, penaltyTotal = 0, effectiveScore = 0, chargedThisWeek = false, debuffed = false });
            }
            return Microsoft.AspNetCore.Http.Results.Ok(await BuildPlayerStatusAsync(active.UserId.Value, db, cfg, httpFactory, false, ct));
        });

        app.MapPost("/api/integrations/minecraft/death-teleport/quote", async (MinecraftDeathTeleportQuoteRequest request, HttpContext http, IConfiguration cfg, MinecraftDbContext db, IHttpClientFactory httpFactory, CancellationToken ct) =>
        {
            if (!IsPluginAuthorized(http, cfg)) return Microsoft.AspNetCore.Http.Results.Unauthorized();
            var active = await FindActiveLinkAsync(db, NormalizeNick(request.Nick), (request.Uuid ?? string.Empty).Trim(), ct);
            var cost = DeathTeleportCost(cfg);
            if (active == null || active.UserId == null)
            {
                return Microsoft.AspNetCore.Http.Results.Ok(new { linked = false, allowed = false, reason = "not-linked", cost, balance = 0, baseRating = 0, adjustmentTotal = 0, spentTotal = 0, restoredTotal = 0 });
            }
            var balance = await BuildMinecraftRatingBalanceAsync(active.UserId.Value, db, cfg, httpFactory, ct);
            var allowed = balance.balance >= cost;
            return Microsoft.AspNetCore.Http.Results.Ok(new
            {
                linked = true,
                allowed,
                reason = allowed ? null : "not-enough-rating",
                cost,
                balance = balance.balance,
                baseRating = balance.baseRating,
                adjustmentTotal = balance.adjustmentTotal,
                spentTotal = balance.spentTotal,
                restoredTotal = balance.restoredTotal,
                nick = active.PlayerName,
                uuid = active.PlayerUuid
            });
        });

        app.MapPost("/api/integrations/minecraft/death-teleport/purchase", async (MinecraftDeathTeleportPurchaseRequest request, HttpContext http, IConfiguration cfg, MinecraftDbContext db, IHttpClientFactory httpFactory, ILogger<Program> logger, CancellationToken ct) =>
        {
            if (!IsPluginAuthorized(http, cfg)) return Microsoft.AspNetCore.Http.Results.Unauthorized();
            var reqId = (request.RequestId ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(reqId) || reqId.Length > 120) return Microsoft.AspNetCore.Http.Results.BadRequest(new { success = false, reason = "bad-request-id" });

            var existing = await db.RatingTransactions.AsNoTracking().FirstOrDefaultAsync(x => x.RequestId == reqId, ct);
            if (existing != null)
            {
                var current = await BuildMinecraftRatingBalanceAsync(existing.UserId, db, cfg, httpFactory, ct);
                return Microsoft.AspNetCore.Http.Results.Ok(new { success = true, duplicate = true, cost = -existing.Delta, newBalance = current.balance, balance = current.balance, baseRating = current.baseRating, adjustmentTotal = current.adjustmentTotal });
            }

            var active = await FindActiveLinkAsync(db, NormalizeNick(request.Nick), (request.Uuid ?? string.Empty).Trim(), ct);
            var cost = DeathTeleportCost(cfg);
            if (active == null || active.UserId == null) return Microsoft.AspNetCore.Http.Results.Ok(new { success = false, reason = "not-linked", cost, balance = 0 });

            var balance = await BuildMinecraftRatingBalanceAsync(active.UserId.Value, db, cfg, httpFactory, ct);
            if (balance.balance < cost)
            {
                return Microsoft.AspNetCore.Http.Results.Ok(new { success = false, reason = "not-enough-rating", cost, balance = balance.balance, baseRating = balance.baseRating, adjustmentTotal = balance.adjustmentTotal });
            }

            var now = DateTimeOffset.UtcNow;
            var metadata = JsonSerializer.Serialize(new { request.DeathId, request.Nick, request.Uuid }, JsonOptions());
            db.RatingTransactions.Add(new MinecraftRatingTransaction
            {
                Id = Guid.NewGuid(),
                UserId = active.UserId.Value,
                PlayerName = active.PlayerName ?? request.Nick,
                PlayerUuid = active.PlayerUuid ?? request.Uuid,
                Delta = -cost,
                Kind = "death-teleport",
                Reason = "Телепорт на место смерти",
                RequestId = reqId,
                MetadataJson = metadata,
                CreatedAtUtc = now
            });
            await db.SaveChangesAsync(ct);
            var updated = await BuildMinecraftRatingBalanceAsync(active.UserId.Value, db, cfg, httpFactory, ct);
            logger.LogInformation("Minecraft rating spent: user={UserId} nick={Nick} cost={Cost} balance={Balance} deathId={DeathId}", active.UserId, active.PlayerName, cost, updated.balance, request.DeathId);
            return Microsoft.AspNetCore.Http.Results.Ok(new { success = true, duplicate = false, cost, newBalance = updated.balance, balance = updated.balance, baseRating = updated.baseRating, adjustmentTotal = updated.adjustmentTotal, spentTotal = updated.spentTotal, restoredTotal = updated.restoredTotal });
        });

        return app;
    }

    private static async Task<MinecraftLink?> FindActiveLinkAsync(MinecraftDbContext db, string? nick, string? uuid, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(uuid))
        {
            var byUuid = await db.Links.FirstOrDefaultAsync(x => x.Confirmed && x.UnlinkedAtUtc == null && x.PlayerUuid == uuid, ct);
            if (byUuid != null) return byUuid;
        }
        if (!string.IsNullOrWhiteSpace(nick))
        {
            var lower = nick.ToLowerInvariant();
            return await db.Links.FirstOrDefaultAsync(x => x.Confirmed && x.UnlinkedAtUtc == null && x.PlayerName != null && x.PlayerName.ToLower() == lower, ct);
        }
        return null;
    }

    private static async Task<object> BuildStatusAsync(Guid userId, MinecraftDbContext db, IConfiguration cfg, IHttpClientFactory httpFactory, CancellationToken ct)
    {
        var active = await db.Links.AsNoTracking().Where(x => x.UserId == userId && x.Confirmed && x.UnlinkedAtUtc == null).OrderByDescending(x => x.ConfirmedAtUtc ?? x.CreatedAt).FirstOrDefaultAsync(ct);
        var linkCount = await db.Links.AsNoTracking().CountAsync(x => x.UserId == userId && x.Confirmed, ct);
        if (active == null)
        {
            return new { linked = false, nick = (string?)null, uuid = (string?)null, linkCount, score = 0, baseRating = 0, minecraftBalance = 0, balance = 0, minecraftSpent = 0, minecraftRestored = 0, minecraftAdjustment = 0, deathTeleportCost = DeathTeleportCost(cfg), weeklyPenaltyCurrent = 0, penaltyTotal = 0, effectiveScore = 0, debuffed = false };
        }
        return await BuildPlayerStatusAsync(userId, db, cfg, httpFactory, false, ct);
    }

    private static async Task<MinecraftPlayerStatusDto> BuildPlayerStatusAsync(Guid userId, MinecraftDbContext db, IConfiguration cfg, IHttpClientFactory httpFactory, bool chargedThisWeek, CancellationToken ct)
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
            balance.deathTeleportCost,
            0,
            balance.spentTotal,
            balance.effectiveRating,
            chargedThisWeek,
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
        int deathTeleportCost,
        int weeklyPenaltyCurrent,
        int penaltyTotal,
        int effectiveScore,
        bool chargedThisWeek,
        bool debuffed);
}
