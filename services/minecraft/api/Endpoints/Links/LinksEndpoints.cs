using System.Data;
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

            var now = DateTimeOffset.UtcNow;
            var oldCodes = await db.LinkCodes.Where(x => x.UserId == uid.Value && (x.UsedAtUtc != null || x.ExpiresAtUtc < now)).ToListAsync(ct);
            if (oldCodes.Count > 0)
            {
                var pendingCodes = oldCodes.Select(x => PendingLinkCode(x.Id)).ToList();
                var stalePendingLinks = await db.Links
                    .Where(x => x.UserId == uid.Value && !x.Confirmed && pendingCodes.Contains(x.Code))
                    .ToListAsync(ct);
                if (stalePendingLinks.Count > 0) db.Links.RemoveRange(stalePendingLinks);
                db.LinkCodes.RemoveRange(oldCodes);
                logger.LogInformation(
                    "Minecraft stale link-code cleanup: user={UserId} removedCodes={RemovedCodes} removedPendingLinks={RemovedPendingLinks}",
                    uid,
                    oldCodes.Count,
                    stalePendingLinks.Count);
            }

            var code = GenerateCode();
            var (salt, hash) = HashCode(code);
            var expires = now.AddMinutes(10);
            var linkCode = new MinecraftLinkCode
            {
                Id = Guid.NewGuid(),
                UserId = uid.Value,
                Nick = nick,
                Salt = salt,
                CodeHash = hash,
                ExpiresAtUtc = expires,
                CreatedAtUtc = now
            };
            db.LinkCodes.Add(linkCode);
            await db.SaveChangesAsync(ct);

            logger.LogInformation(
                "Minecraft link code generated: user={UserId} nick={Nick} codeId={CodeId} codeFp={CodeFingerprint} expiresAt={ExpiresAt}",
                uid,
                nick,
                linkCode.Id,
                SecretFingerprint(code),
                expires);

            var deliveryResult = await SendLinkCodeAsync(nick, code, cfg, httpFactory, logger, ct);
            if (!deliveryResult.Ok)
            {
                db.LinkCodes.Remove(linkCode);
                await db.SaveChangesAsync(ct);
                logger.LogWarning(
                    "Minecraft link code discarded because delivery failed: user={UserId} nick={Nick} codeId={CodeId} attempted={Attempted} status={StatusCode} message={Message}",
                    uid,
                    nick,
                    linkCode.Id,
                    deliveryResult.Attempted,
                    deliveryResult.StatusCode,
                    deliveryResult.Message);

                return Microsoft.AspNetCore.Http.Results.Json(new
                {
                    message = deliveryResult.Attempted
                        ? $"Не удалось отправить код в Minecraft: {deliveryResult.Message}"
                        : $"Отправка кода в Minecraft не настроена: {deliveryResult.Message}",
                    delivery = new
                    {
                        attempted = deliveryResult.Attempted,
                        delivered = false,
                        message = deliveryResult.Message,
                        statusCode = deliveryResult.StatusCode
                    }
                }, statusCode: deliveryResult.Attempted
                    ? StatusCodes.Status502BadGateway
                    : StatusCodes.Status503ServiceUnavailable);
            }

            var deliveredUuid = NormalizeUuid(deliveryResult.PlayerUuid);
            if (!string.IsNullOrWhiteSpace(deliveredUuid))
            {
                var existingOwner = await db.Links.AsNoTracking().FirstOrDefaultAsync(x => x.Confirmed
                    && x.UnlinkedAtUtc == null
                    && x.PlayerUuid != null
                    && x.PlayerUuid.ToLower() == deliveredUuid, ct);
                if (existingOwner is not null)
                {
                    db.LinkCodes.Remove(linkCode);
                    await db.SaveChangesAsync(ct);
                    logger.LogWarning(
                        "Minecraft link request rejected after delivery because UUID is already active: requester={RequesterUserId} owner={OwnerUserId} linkId={LinkId} nick={Nick} uuid={Uuid}; rating ledger untouched",
                        uid,
                        existingOwner.UserId,
                        existingOwner.Id,
                        nick,
                        deliveredUuid);
                    return Microsoft.AspNetCore.Http.Results.Conflict(new
                    {
                        message = existingOwner.UserId == uid.Value
                            ? "Этот Minecraft-профиль уже привязан к вашей учётной записи."
                            : "Этот Minecraft-профиль уже привязан к другой учётной записи."
                    });
                }
            }

            var pendingLink = new MinecraftLink
            {
                Id = Guid.NewGuid(),
                UserId = uid.Value,
                PlayerName = nick,
                PlayerUuid = string.IsNullOrWhiteSpace(deliveredUuid) ? null : deliveredUuid,
                Code = PendingLinkCode(linkCode.Id),
                Confirmed = false,
                CreatedAt = now
            };
            db.Links.Add(pendingLink);
            await db.SaveChangesAsync(ct);
            logger.LogInformation(
                "Minecraft pending link identity saved after in-game delivery: user={UserId} pendingLinkId={PendingLinkId} codeId={CodeId} nick={Nick} uuid={Uuid} uuidPresent={UuidPresent}",
                uid,
                pendingLink.Id,
                linkCode.Id,
                nick,
                pendingLink.PlayerUuid,
                !string.IsNullOrWhiteSpace(pendingLink.PlayerUuid));

            var status = await BuildStatusAsync(uid.Value, db, cfg, httpFactory, ct);
            return Microsoft.AspNetCore.Http.Results.Ok(new
            {
                expiresAtUtc = expires,
                expiresInSeconds = (int)Math.Max(0, (expires - DateTimeOffset.UtcNow).TotalSeconds),
                status,
                delivery = new
                {
                    attempted = deliveryResult.Attempted,
                    delivered = true,
                    message = deliveryResult.Message,
                    statusCode = deliveryResult.StatusCode
                }
            });
        });

        app.MapPost("/api/integrations/minecraft/confirm", async (MinecraftConfirmRequest req, HttpContext http, IConfiguration cfg, MinecraftDbContext db, IHttpClientFactory httpFactory, ILogger<Program> logger, CancellationToken ct) =>
        {
            var uid = UserId(http, cfg);
            if (uid == null) return Unauthorized();
            var code = (req.Code ?? string.Empty).Trim().ToUpperInvariant();
            if (code.Length < 4 || code.Length > 32) return Microsoft.AspNetCore.Http.Results.BadRequest(new { message = "Неверный код." });

            var now = DateTimeOffset.UtcNow;
            await using var linkTransaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
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

            var pendingCode = PendingLinkCode(match.Id);
            var pendingLink = await db.Links.FirstOrDefaultAsync(x => x.UserId == uid.Value && !x.Confirmed && x.Code == pendingCode, ct);
            var confirmedUuid = NormalizeUuid(req.PlayerUuid);
            if (string.IsNullOrWhiteSpace(confirmedUuid)) confirmedUuid = NormalizeUuid(pendingLink?.PlayerUuid);
            if (!string.IsNullOrWhiteSpace(confirmedUuid))
            {
                if (db.Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true)
                {
                    var lockKey = "minecraft-link:" + confirmedUuid;
                    await db.Database.ExecuteSqlInterpolatedAsync(
                        $"SELECT pg_advisory_xact_lock(hashtextextended({lockKey}, 0))",
                        ct);
                }

                var uuidOwner = await db.Links.AsNoTracking().FirstOrDefaultAsync(x => x.Confirmed
                    && x.UnlinkedAtUtc == null
                    && x.PlayerUuid != null
                    && x.PlayerUuid.ToLower() == confirmedUuid, ct);
                if (uuidOwner is not null)
                {
                    if (pendingLink is not null) db.Links.Remove(pendingLink);
                    db.LinkCodes.Remove(match);
                    await db.SaveChangesAsync(ct);
                    await linkTransaction.CommitAsync(ct);
                    logger.LogWarning(
                        "Minecraft confirmation rejected and pending identity invalidated because UUID is already active: requester={RequesterUserId} owner={OwnerUserId} linkId={LinkId} nick={Nick} uuid={Uuid}; rating ledger untouched",
                        uid,
                        uuidOwner.UserId,
                        uuidOwner.Id,
                        match.Nick,
                        confirmedUuid);
                    return Microsoft.AspNetCore.Http.Results.Conflict(new
                    {
                        message = uuidOwner.UserId == uid.Value
                            ? "Этот Minecraft-профиль уже привязан к вашей учётной записи."
                            : "Этот Minecraft UUID уже привязан к другой учётной записи."
                    });
                }
            }

            match.UsedAtUtc = now;
            MinecraftLink link;
            if (pendingLink is not null)
            {
                link = pendingLink;
                link.PlayerName = match.Nick;
                link.PlayerUuid = string.IsNullOrWhiteSpace(confirmedUuid) ? null : confirmedUuid;
                link.Code = "LINK-" + Guid.NewGuid().ToString("N");
                link.Confirmed = true;
                link.ConfirmedAtUtc = now;
                link.UnlinkedAtUtc = null;
                logger.LogInformation(
                    "Minecraft confirmation is promoting pending delivered identity: user={UserId} pendingLinkId={PendingLinkId} codeId={CodeId} nick={Nick} uuid={Uuid} uuidPresent={UuidPresent}",
                    uid,
                    pendingLink.Id,
                    match.Id,
                    match.Nick,
                    link.PlayerUuid,
                    !string.IsNullOrWhiteSpace(link.PlayerUuid));
            }
            else
            {
                link = new MinecraftLink
                {
                    Id = Guid.NewGuid(),
                    UserId = uid.Value,
                    PlayerName = match.Nick,
                    PlayerUuid = string.IsNullOrWhiteSpace(confirmedUuid) ? null : confirmedUuid,
                    Code = "LINK-" + Guid.NewGuid().ToString("N"),
                    Confirmed = true,
                    CreatedAt = now,
                    ConfirmedAtUtc = now
                };
                db.Links.Add(link);
                logger.LogWarning(
                    "Minecraft confirmation had no pending delivered identity; compatibility link created and UUID will be bound by authenticated status refresh: user={UserId} codeId={CodeId} nick={Nick} uuid={Uuid}",
                    uid,
                    match.Id,
                    match.Nick,
                    link.PlayerUuid);
            }
            await db.SaveChangesAsync(ct);
            await linkTransaction.CommitAsync(ct);
            await AssignMinecraftRoleAsync(uid.Value, cfg, httpFactory, logger, ct);
            logger.LogInformation(
                "Minecraft linked with unlimited active-link policy: user={UserId} linkId={LinkId} nick={Nick} uuid={Uuid} uuidPresent={UuidPresent}; shared user rating ledger was not reset or restored",
                uid,
                link.Id,
                match.Nick,
                link.PlayerUuid,
                !string.IsNullOrWhiteSpace(link.PlayerUuid));
            var status = await BuildStatusAsync(uid.Value, db, cfg, httpFactory, ct);
            return Microsoft.AspNetCore.Http.Results.Ok(status);
        });

        app.MapDelete("/api/integrations/minecraft/links/{linkId:guid}", async (Guid linkId, HttpContext http, IConfiguration cfg, MinecraftDbContext db, IHttpClientFactory httpFactory, ILogger<Program> logger, CancellationToken ct) =>
        {
            var uid = UserId(http, cfg);
            if (uid == null) return Unauthorized();

            var link = await db.Links.FirstOrDefaultAsync(x => x.Id == linkId
                && x.UserId == uid.Value
                && x.Confirmed
                && x.UnlinkedAtUtc == null, ct);
            if (link is null) return Microsoft.AspNetCore.Http.Results.NotFound(new { message = "Активная привязка не найдена." });

            link.UnlinkedAtUtc = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);

            var activeLinksRemaining = await db.Links.AsNoTracking().CountAsync(x => x.UserId == uid.Value
                && x.Confirmed
                && x.UnlinkedAtUtc == null, ct);
            if (activeLinksRemaining == 0)
                await RemoveMinecraftRoleAsync(uid.Value, cfg, httpFactory, logger, ct);

            logger.LogInformation(
                "Minecraft single link removed: user={UserId} linkId={LinkId} nick={Nick} uuid={Uuid} activeLinksRemaining={ActiveLinksRemaining}; rating transactions were not created, deleted or changed",
                uid,
                link.Id,
                link.PlayerName,
                link.PlayerUuid,
                activeLinksRemaining);

            var status = await BuildStatusAsync(uid.Value, db, cfg, httpFactory, ct);
            return Microsoft.AspNetCore.Http.Results.Ok(status);
        });

        // Compatibility endpoint: explicitly removes every active Minecraft identity for the user.
        // The user-level rating ledger is intentionally preserved.
        app.MapDelete("/api/integrations/minecraft/unlink", async (HttpContext http, IConfiguration cfg, MinecraftDbContext db, IHttpClientFactory httpFactory, ILogger<Program> logger, CancellationToken ct) =>
        {
            var uid = UserId(http, cfg);
            if (uid == null) return Unauthorized();
            var now = DateTimeOffset.UtcNow;
            var links = await db.Links.Where(x => x.UserId == uid.Value && x.Confirmed && x.UnlinkedAtUtc == null).ToListAsync(ct);
            foreach (var link in links) link.UnlinkedAtUtc = now;
            await db.SaveChangesAsync(ct);
            await RemoveMinecraftRoleAsync(uid.Value, cfg, httpFactory, logger, ct);
            logger.LogInformation(
                "Minecraft all links removed: user={UserId} removed={Removed}; rating transactions were not created, deleted or changed",
                uid,
                links.Count);
            return Microsoft.AspNetCore.Http.Results.Ok(new { linked = false, linkCount = 0, links = Array.Empty<object>() });
        });

        app.MapGet("/api/integrations/minecraft/economy", (IConfiguration cfg) =>
            Microsoft.AspNetCore.Http.Results.Ok(new
            {
                deathCoordinatesCost = DeathCoordinatesCost(cfg),
                deathChestCost = DeathChestCost(cfg),
                deathTeleportCost = DeathTeleportCost(cfg),
                deathInventoryCost = DeathInventoryCost(cfg),
                deathChestAndTeleportCost = DeathChestCost(cfg) + DeathTeleportCost(cfg)
            }));

        app.MapPost("/api/integrations/minecraft/events/join", async (MinecraftJoinEventRequest request, HttpContext http, IConfiguration cfg, MinecraftDbContext db, IHttpClientFactory httpFactory, ILogger<Program> logger, CancellationToken ct) =>
        {
            if (!IsPluginAuthorized(http, cfg)) return Microsoft.AspNetCore.Http.Results.Unauthorized();
            var nick = NormalizeNick(request.Nick);
            if (!IsValidNick(nick)) return Microsoft.AspNetCore.Http.Results.BadRequest(new { message = "bad nick" });
            var uuid = NormalizeUuid(request.Uuid);
            logger.LogInformation("Minecraft join status request: nick={Nick} uuid={Uuid} remote={Remote}", nick, uuid, http.Connection.RemoteIpAddress);
            var active = await FindActiveLinkAsync(db, nick, uuid, logger, ct);
            if (active == null || active.UserId == null)
            {
                logger.LogInformation("Minecraft join resolved as unlinked: nick={Nick} uuid={Uuid}", nick, uuid);
                return Microsoft.AspNetCore.Http.Results.Ok(UnlinkedPluginStatus(nick, uuid, cfg));
            }

            await BindLinkIdentityAsync(db, active, nick, uuid, logger, "join", ct);
            var dto = await BuildPlayerStatusAsync(active, db, cfg, httpFactory, ct);
            logger.LogInformation("Minecraft join resolved as linked: nick={Nick} uuid={Uuid} user={UserId} balance={Balance}", nick, uuid, active.UserId, dto.minecraftBalance);
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
            var normalizedUuid = NormalizeUuid(uuid);
            logger.LogInformation("Minecraft player-status start: nick={Nick} uuid={Uuid} nickPresent={NickPresent} uuidPresent={UuidPresent}", normalizedNick, normalizedUuid, !string.IsNullOrWhiteSpace(normalizedNick), !string.IsNullOrWhiteSpace(normalizedUuid));
            var active = await FindActiveLinkAsync(db, normalizedNick, normalizedUuid, logger, ct);
            if (active == null || active.UserId == null)
            {
                logger.LogInformation("Minecraft player-status result: linked=false nick={Nick} uuid={Uuid}; no unique active UUID match and no unique UUID-less exact-nick fallback", normalizedNick, normalizedUuid);
                return Microsoft.AspNetCore.Http.Results.Ok(UnlinkedPluginStatus(normalizedNick, normalizedUuid, cfg));
            }

            await BindLinkIdentityAsync(db, active, normalizedNick, normalizedUuid, logger, "player-status", ct);
            var dto = await BuildPlayerStatusAsync(active, db, cfg, httpFactory, ct);
            logger.LogInformation("Minecraft player-status result: linked=true nick={Nick} uuid={Uuid} linkId={LinkId} userId={UserId} storedNick={StoredNick} storedUuid={StoredUuid} balance={Balance}", normalizedNick, normalizedUuid, active.Id, active.UserId, active.PlayerName, active.PlayerUuid, dto.minecraftBalance);
            return Microsoft.AspNetCore.Http.Results.Ok(dto);
        });

        return app;
    }

    private static string NormalizeUuid(string? value) => (value ?? string.Empty).Trim().ToLowerInvariant();

    private static string PendingLinkCode(Guid codeId) => "PENDING-" + codeId.ToString("N");

    private static object UnlinkedPluginStatus(string? nick, string? uuid, IConfiguration cfg) => new
    {
        linked = false,
        nick = NormalizeNick(nick),
        uuid = NormalizeUuid(uuid),
        linkCount = 0,
        minecraftBalance = 0,
        balance = 0
    };

    private static async Task<MinecraftLink?> FindActiveLinkAsync(MinecraftDbContext db, string? nick, string? uuid, ILogger<Program> logger, CancellationToken ct)
    {
        var normalizedUuid = NormalizeUuid(uuid);
        if (!string.IsNullOrWhiteSpace(normalizedUuid))
        {
            var uuidMatches = await db.Links
                .Where(x => x.Confirmed
                    && x.UnlinkedAtUtc == null
                    && x.PlayerUuid != null
                    && x.PlayerUuid.ToLower() == normalizedUuid)
                .OrderByDescending(x => x.ConfirmedAtUtc ?? x.CreatedAt)
                .Take(2)
                .ToListAsync(ct);
            if (uuidMatches.Count == 1) return uuidMatches[0];
            if (uuidMatches.Count > 1)
            {
                logger.LogCritical(
                    "Minecraft UUID has multiple active links; refusing ambiguous identity resolution to protect the shared balance: uuid={Uuid} linkIds={LinkIds} userIds={UserIds}",
                    normalizedUuid,
                    string.Join(',', uuidMatches.Select(x => x.Id)),
                    string.Join(',', uuidMatches.Select(x => x.UserId)));
                return null;
            }
        }

        // The exact-nick fallback is allowed only for a unique active legacy/newly-confirmed link
        // that still has no UUID. This prevents nickname recycling from stealing a UUID-bound link.
        if (!string.IsNullOrWhiteSpace(nick))
        {
            var lower = nick.Trim().ToLowerInvariant();
            var candidates = await db.Links
                .Where(x => x.Confirmed
                    && x.UnlinkedAtUtc == null
                    && (x.PlayerUuid == null || x.PlayerUuid == string.Empty)
                    && x.PlayerName != null
                    && x.PlayerName.ToLower() == lower)
                .OrderByDescending(x => x.ConfirmedAtUtc ?? x.CreatedAt)
                .Take(2)
                .ToListAsync(ct);
            return candidates.Count == 1 ? candidates[0] : null;
        }
        return null;
    }

    private static async Task BindLinkIdentityAsync(
        MinecraftDbContext db,
        MinecraftLink active,
        string? nick,
        string? uuid,
        ILogger<Program> logger,
        string source,
        CancellationToken ct)
    {
        var normalizedNick = NormalizeNick(nick);
        var normalizedUuid = NormalizeUuid(uuid);
        var changed = false;
        var previousNick = active.PlayerName;
        var previousUuid = active.PlayerUuid;

        if (!string.IsNullOrWhiteSpace(normalizedUuid) && string.IsNullOrWhiteSpace(active.PlayerUuid))
        {
            active.PlayerUuid = normalizedUuid;
            changed = true;
        }
        if (IsValidNick(normalizedNick) && !string.Equals(active.PlayerName, normalizedNick, StringComparison.Ordinal))
        {
            active.PlayerName = normalizedNick;
            changed = true;
        }

        if (!changed) return;

        await db.SaveChangesAsync(ct);
        logger.LogInformation(
            "Minecraft active link identity bound: source={Source} linkId={LinkId} userId={UserId} previousNick={PreviousNick} newNick={NewNick} previousUuid={PreviousUuid} newUuid={NewUuid}",
            source,
            active.Id,
            active.UserId,
            previousNick,
            active.PlayerName,
            previousUuid,
            active.PlayerUuid);
    }

    // This response is returned to the website user. It deliberately exposes only active nicknames
    // and the one shared user-level Minecraft balance. Link/unlink operations never reset or restore it.
    private static async Task<object> BuildStatusAsync(Guid userId, MinecraftDbContext db, IConfiguration cfg, IHttpClientFactory httpFactory, CancellationToken ct)
    {
        var activeLinks = await db.Links.AsNoTracking()
            .Where(x => x.UserId == userId && x.Confirmed && x.UnlinkedAtUtc == null)
            .OrderByDescending(x => x.ConfirmedAtUtc ?? x.CreatedAt)
            .ToListAsync(ct);
        var active = activeLinks.FirstOrDefault();
        var links = activeLinks.Select(x => new
        {
            id = x.Id,
            nick = x.PlayerName,
            linkedAtUtc = x.ConfirmedAtUtc ?? x.CreatedAt
        }).ToArray();

        if (active == null)
        {
            return new
            {
                linked = false,
                nick = (string?)null,
                linkedAtUtc = (DateTimeOffset?)null,
                linkCount = 0,
                links,
                minecraftBalance = 0,
                balance = 0
            };
        }

        var balance = await BuildMinecraftRatingBalanceAsync(userId, db, cfg, httpFactory, ct);
        return new
        {
            linked = true,
            nick = active.PlayerName,
            linkedAtUtc = active.ConfirmedAtUtc ?? active.CreatedAt,
            linkCount = activeLinks.Count,
            links,
            minecraftBalance = balance.balance,
            balance = balance.balance
        };
    }

    private static async Task<MinecraftPlayerStatusDto> BuildPlayerStatusAsync(MinecraftLink active, MinecraftDbContext db, IConfiguration cfg, IHttpClientFactory httpFactory, CancellationToken ct)
    {
        if (active.UserId is not Guid userId)
            throw new InvalidOperationException("Active Minecraft link has no user id.");

        var linkCount = await db.Links.AsNoTracking().CountAsync(x => x.UserId == userId
            && x.Confirmed
            && x.UnlinkedAtUtc == null, ct);
        var balance = await BuildMinecraftRatingBalanceAsync(userId, db, cfg, httpFactory, ct);
        return new MinecraftPlayerStatusDto(
            true,
            active.PlayerName,
            active.PlayerUuid,
            active.PlayerName,
            active.PlayerUuid,
            active.PlayerName,
            active.PlayerUuid,
            active.ConfirmedAtUtc ?? active.CreatedAt,
            linkCount,
            balance.balance,
            balance.balance);
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
        int minecraftBalance,
        int balance);
}
