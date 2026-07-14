using System.Data;
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
    private static readonly HashSet<string> DeathRecoveryActions = new(StringComparer.OrdinalIgnoreCase)
    {
        "coordinates",
        "chest",
        "return",
        "both"
    };

    private static WebApplication MapDeathRecoveryEndpoints(WebApplication app)
    {
        const string prefix = "/api/integrations/minecraft/death-recovery";

        app.MapGet(prefix + "/health", async (
            Guid? playerUuid,
            HttpContext http,
            IConfiguration cfg,
            MinecraftDbContext db,
            IHttpClientFactory httpFactory,
            ILogger<Program> logger,
            CancellationToken ct) =>
        {
            if (!IsPluginAuthorized(http, cfg))
            {
                logger.LogWarning("Death recovery health unauthorized: playerUuid={PlayerUuid} remote={Remote}", playerUuid, http.Connection.RemoteIpAddress);
                return Microsoft.AspNetCore.Http.Results.Unauthorized();
            }

            logger.LogInformation("Death recovery health start: playerUuid={PlayerUuid} remote={Remote}", playerUuid, http.Connection.RemoteIpAddress);
            try
            {
                if (!await db.Database.CanConnectAsync(ct))
                {
                    logger.LogWarning("Death recovery health failed: minecraft database unavailable playerUuid={PlayerUuid}", playerUuid);
                    return Microsoft.AspNetCore.Http.Results.Json(
                        new { available = false, reason = "minecraft-database-unavailable" },
                        statusCode: StatusCodes.Status503ServiceUnavailable);
                }

                if (!await AreRatingBackendsHealthyAsync(cfg, httpFactory, ct))
                {
                    logger.LogWarning("Death recovery health failed: rating backends unavailable playerUuid={PlayerUuid}", playerUuid);
                    return Microsoft.AspNetCore.Http.Results.Json(
                        new { available = false, reason = "rating-backend-unavailable" },
                        statusCode: StatusCodes.Status503ServiceUnavailable);
                }

                Guid? userId = null;
                if (playerUuid is Guid requestedUuid && requestedUuid != Guid.Empty)
                {
                    userId = await FindLinkedUserIdAsync(db, requestedUuid, null, logger, ct);
                }
                else
                {
                    userId = await db.Links.AsNoTracking()
                        .Where(x => x.Confirmed && x.UnlinkedAtUtc == null && x.UserId != null)
                        .Select(x => x.UserId)
                        .FirstOrDefaultAsync(ct);
                }

                if (userId is Guid linkedUserId && linkedUserId != Guid.Empty)
                {
                    var rating = await TryLoadActivitySummaryStrictAsync(linkedUserId, cfg, httpFactory, ct);
                    if (!rating.Available)
                    {
                        return Microsoft.AspNetCore.Http.Results.Json(
                            new { available = false, reason = "rating-data-unavailable" },
                            statusCode: StatusCodes.Status503ServiceUnavailable);
                    }
                }

                var linked = userId is Guid id && id != Guid.Empty;
                logger.LogInformation("Death recovery health success: playerUuid={PlayerUuid} linked={Linked} userId={UserId}", playerUuid, linked, userId);
                return Microsoft.AspNetCore.Http.Results.Ok(new
                {
                    available = true,
                    linked
                });
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning("Death recovery health timed out: playerUuid={PlayerUuid}", playerUuid);
                return Microsoft.AspNetCore.Http.Results.Json(
                    new { available = false, reason = "backend-timeout" },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Death recovery health failed unexpectedly: playerUuid={PlayerUuid}", playerUuid);
                return Microsoft.AspNetCore.Http.Results.Json(
                    new { available = false, reason = "minecraft-backend-unavailable" },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        app.MapPut(prefix + "/{deathId:guid}", async (
            Guid deathId,
            MinecraftDeathRecoveryStateRequest request,
            HttpContext http,
            IConfiguration cfg,
            MinecraftDbContext db,
            ILogger<Program> logger,
            CancellationToken ct) =>
        {
            if (!IsPluginAuthorized(http, cfg))
            {
                logger.LogWarning("Death recovery state unauthorized: deathId={DeathId} playerUuid={PlayerUuid} remote={Remote}", deathId, request.PlayerUuid, http.Connection.RemoteIpAddress);
                return Microsoft.AspNetCore.Http.Results.Unauthorized();
            }
            logger.LogInformation(
                "Death recovery state start: deathId={DeathId} playerUuid={PlayerUuid} player={PlayerName} stage={Stage} action={Action} revision={Revision} itemsLength={ItemsLength} itemsResolved={ItemsResolved} dropsReleased={DropsReleased} chestCreated={ChestCreated} backendUnavailable={BackendUnavailable}",
                deathId,
                request.PlayerUuid,
                request.PlayerName,
                request.Stage,
                request.Action,
                request.Revision,
                request.ItemsPayload?.Length ?? 0,
                request.ItemsResolved,
                request.DropsReleased,
                request.ChestCreated,
                request.BackendUnavailable);
            if (request.DeathId != deathId || request.PlayerUuid == Guid.Empty || request.WorldUuid == Guid.Empty)
                return Microsoft.AspNetCore.Http.Results.BadRequest(new { message = "invalid death recovery identity" });
            if (string.IsNullOrWhiteSpace(request.PlayerName)
                || string.IsNullOrWhiteSpace(request.WorldKey)
                || string.IsNullOrWhiteSpace(request.WorldName))
                return Microsoft.AspNetCore.Http.Results.BadRequest(new { message = "missing world or player data" });

            var entity = await db.DeathRecoveries.FirstOrDefaultAsync(x => x.DeathId == deathId, ct);
            var linkedUserId = entity?.UserId ?? await FindLinkedUserIdAsync(db, request.PlayerUuid, request.PlayerName, logger, ct);
            logger.LogInformation(
                "Death recovery link resolution: deathId={DeathId} entityExists={EntityExists} existingUserId={ExistingUserId} resolvedUserId={ResolvedUserId}",
                deathId,
                entity is not null,
                entity?.UserId,
                linkedUserId);

            if (entity is null && linkedUserId is null)
            {
                logger.LogWarning("Death recovery state rejected because player is not linked: deathId={DeathId} playerUuid={PlayerUuid} player={PlayerName}", deathId, request.PlayerUuid, request.PlayerName);
                return Microsoft.AspNetCore.Http.Results.Conflict(new
                {
                    success = false,
                    linked = false,
                    reason = "not-linked"
                });
            }

            if (entity is null)
            {
                entity = new MinecraftDeathRecovery
                {
                    DeathId = deathId,
                    UserId = linkedUserId,
                    CreatedAtUtc = request.CreatedAtUtc ?? DateTimeOffset.UtcNow,
                    OfferExpiresAtUtc = request.OfferExpiresAtUtc ?? DateTimeOffset.UtcNow,
                    UpdatedAtUtc = DateTimeOffset.UtcNow
                };
                db.DeathRecoveries.Add(entity);
                logger.LogInformation("Death recovery row created in change tracker: deathId={DeathId} userId={UserId}", deathId, linkedUserId);
            }
            else
            {
                if (entity.PlayerUuid != Guid.Empty && entity.PlayerUuid != request.PlayerUuid)
                {
                    logger.LogWarning("Death recovery player mismatch: deathId={DeathId} existing={ExistingPlayerUuid} incoming={IncomingPlayerUuid}", deathId, entity.PlayerUuid, request.PlayerUuid);
                    return Microsoft.AspNetCore.Http.Results.Conflict(new { message = "death recovery player mismatch" });
                }
                if (request.Revision < entity.Revision)
                {
                    logger.LogInformation("Death recovery stale revision ignored: deathId={DeathId} incoming={IncomingRevision} stored={StoredRevision}", deathId, request.Revision, entity.Revision);
                    return Microsoft.AspNetCore.Http.Results.Ok(ToDeathRecoveryDto(entity));
                }
                if (entity.UserId is null && linkedUserId is null && !request.ItemsResolved)
                {
                    logger.LogWarning("Legacy unlinked death recovery row rejected: deathId={DeathId} playerUuid={PlayerUuid}", deathId, request.PlayerUuid);
                    return Microsoft.AspNetCore.Http.Results.Conflict(new
                    {
                        success = false,
                        linked = false,
                        reason = "not-linked"
                    });
                }
            }

            entity.UserId ??= linkedUserId;
            entity.PlayerUuid = request.PlayerUuid;
            entity.PlayerName = Limit(request.PlayerName, 32);
            entity.WorldUuid = request.WorldUuid;
            entity.WorldKey = Limit(request.WorldKey, 160);
            entity.WorldName = Limit(request.WorldName, 160);
            entity.X = request.X;
            entity.Y = request.Y;
            entity.Z = request.Z;
            entity.Yaw = request.Yaw;
            entity.Pitch = request.Pitch;

            if (!entity.ItemsResolved && !string.IsNullOrWhiteSpace(request.ItemsPayload))
                entity.ItemsPayload = request.ItemsPayload;
            else if (string.IsNullOrWhiteSpace(entity.ItemsPayload))
                entity.ItemsPayload = request.ItemsPayload ?? string.Empty;

            entity.CreatedAtUtc = request.CreatedAtUtc ?? (entity.CreatedAtUtc == default ? DateTimeOffset.UtcNow : entity.CreatedAtUtc);
            entity.OfferExpiresAtUtc = request.OfferExpiresAtUtc ?? entity.OfferExpiresAtUtc;
            entity.RescueEndsAtUtc = request.RescueEndsAtUtc;

            var requestedStage = Limit(request.Stage ?? entity.Stage, 64);
            if (!IsTerminalDeathStage(entity.Stage) || IsTerminalDeathStage(requestedStage))
                entity.Stage = requestedStage;

            var incomingAction = NullIfBlank(request.Action, 32)?.ToLowerInvariant();
            if (incomingAction is not null)
            {
                var storedAction = NullIfBlank(entity.Action, 32)?.ToLowerInvariant();
                if (storedAction is null || storedAction == incomingAction)
                {
                    entity.Action = incomingAction;
                }
                else
                {
                    logger.LogWarning(
                        "Death recovery ignored action rewrite after single-choice selection: deathId={DeathId} storedAction={StoredAction} incomingAction={IncomingAction}",
                        deathId,
                        storedAction,
                        incomingAction);
                }
            }

            var incomingPaymentStatus = Limit(request.PaymentStatus ?? "none", 32);
            if (PaymentStatusRank(incomingPaymentStatus) >= PaymentStatusRank(entity.PaymentStatus))
            {
                entity.PaymentStatus = incomingPaymentStatus;
                entity.PaymentErrorCode = NullIfBlank(request.PaymentErrorCode, 128);
                var incomingRequestId = NullIfBlank(request.PurchaseRequestId, 120);
                if (incomingRequestId is not null)
                    entity.PurchaseRequestId = incomingRequestId;
                entity.ChargedAmount = Math.Max(entity.ChargedAmount, request.ChargedAmount);
            }

            entity.DropsReleased |= request.DropsReleased;
            entity.ChestSpotReserved |= request.ChestSpotReserved;
            entity.ChestCreated |= request.ChestCreated;
            entity.ItemsResolved |= request.ItemsResolved;
            entity.RescueCompleted |= request.RescueCompleted;
            entity.RescuePending = entity.RescueCompleted ? false : request.RescuePending;
            entity.PreviousGameMode = NullIfBlank(request.PreviousGameMode, 32) ?? entity.PreviousGameMode;
            entity.BackendUnavailable |= request.BackendUnavailable;
            entity.Compensated |= request.Compensated;
            entity.CompensationPending = entity.Compensated ? false : request.CompensationPending;

            if (request.ChestSpotReserved || request.ChestCreated)
            {
                entity.ChestX = request.ChestX ?? entity.ChestX;
                entity.ChestY = request.ChestY ?? entity.ChestY;
                entity.ChestZ = request.ChestZ ?? entity.ChestZ;
                entity.ChestSecondX = request.ChestSecondX ?? entity.ChestSecondX;
                entity.ChestSecondY = request.ChestSecondY ?? entity.ChestSecondY;
                entity.ChestSecondZ = request.ChestSecondZ ?? entity.ChestSecondZ;
            }

            entity.FinalX = request.FinalX ?? entity.FinalX;
            entity.FinalY = request.FinalY ?? entity.FinalY;
            entity.FinalZ = request.FinalZ ?? entity.FinalZ;
            entity.LastError = NullIfBlank(request.LastError, 1024);
            entity.Revision = Math.Max(entity.Revision, request.Revision);
            entity.UpdatedAtUtc = DateTimeOffset.UtcNow;

            await db.SaveChangesAsync(ct);
            logger.LogInformation(
                "Death recovery state saved: deathId={DeathId} userId={UserId} stage={Stage} action={Action} revision={Revision} itemsResolved={ItemsResolved} dropsReleased={DropsReleased} chestCreated={ChestCreated} paymentStatus={PaymentStatus}",
                entity.DeathId,
                entity.UserId,
                entity.Stage,
                entity.Action,
                entity.Revision,
                entity.ItemsResolved,
                entity.DropsReleased,
                entity.ChestCreated,
                entity.PaymentStatus);
            return Microsoft.AspNetCore.Http.Results.Ok(ToDeathRecoveryDto(entity));
        });

        app.MapGet(prefix + "/player/{playerUuid:guid}/pending", async (
            Guid playerUuid,
            HttpContext http,
            IConfiguration cfg,
            MinecraftDbContext db,
            CancellationToken ct) =>
        {
            if (!IsPluginAuthorized(http, cfg))
                return Microsoft.AspNetCore.Http.Results.Unauthorized();

            var rows = await db.DeathRecoveries.AsNoTracking()
                .Where(x => x.PlayerUuid == playerUuid
                    && (!x.ItemsResolved || x.RescuePending || x.CompensationPending))
                .OrderBy(x => x.CreatedAtUtc)
                .Take(100)
                .ToListAsync(ct);
            return Microsoft.AspNetCore.Http.Results.Ok(rows.Select(ToDeathRecoveryDto).ToArray());
        });

        app.MapPost(prefix + "/{deathId:guid}/purchase", async (
            Guid deathId,
            MinecraftDeathRecoveryPurchaseRequest request,
            HttpContext http,
            IConfiguration cfg,
            MinecraftDbContext db,
            IHttpClientFactory httpFactory,
            ILogger<Program> logger,
            CancellationToken ct) =>
        {
            if (!IsPluginAuthorized(http, cfg))
                return Microsoft.AspNetCore.Http.Results.Unauthorized();

            var action = (request.Action ?? string.Empty).Trim().ToLowerInvariant();
            var requestId = (request.RequestId ?? string.Empty).Trim();
            if (!DeathRecoveryActions.Contains(action)
                || request.PlayerUuid == Guid.Empty
                || string.IsNullOrWhiteSpace(requestId)
                || requestId.Length > 120)
            {
                return Microsoft.AspNetCore.Http.Results.BadRequest(new { success = false, reason = "bad-request" });
            }

            var expectedAmount = action switch
            {
                "coordinates" => DeathCoordinatesCost(cfg),
                "chest" => DeathChestCost(cfg),
                "return" => DeathTeleportCost(cfg),
                _ => DeathChestCost(cfg) + DeathTeleportCost(cfg)
            };
            if (request.Amount != expectedAmount)
                return Microsoft.AspNetCore.Http.Results.BadRequest(new { success = false, reason = "cost-mismatch", cost = expectedAmount });

            var refundRequestId = "death-refund:" + requestId;
            if (await db.RatingTransactions.AsNoTracking().AnyAsync(x => x.RequestId == refundRequestId, ct))
            {
                return Microsoft.AspNetCore.Http.Results.Conflict(new
                {
                    success = false,
                    reason = "purchase-already-compensated",
                    requestId
                });
            }

            logger.LogInformation(
                "Minecraft death purchase start: death={DeathId} player={PlayerUuid} action={Action} amount={Amount} request={RequestId}",
                deathId,
                request.PlayerUuid,
                action,
                request.Amount,
                requestId);

            var existing = await db.RatingTransactions.AsNoTracking()
                .FirstOrDefaultAsync(x => x.RequestId == requestId, ct);
            if (existing is not null)
            {
                if (!TransactionMatchesPurchase(existing, deathId, action, request.PlayerUuid))
                {
                    logger.LogError(
                        "Minecraft death purchase request-id collision: death={DeathId} action={Action} request={RequestId} existingDelta={Delta} existingUser={UserId}",
                        deathId,
                        action,
                        requestId,
                        existing.Delta,
                        existing.UserId);
                    return Microsoft.AspNetCore.Http.Results.Conflict(new { success = false, reason = "request-id-collision" });
                }
                logger.LogInformation(
                    "Minecraft death purchase duplicate accepted: death={DeathId} action={Action} request={RequestId} charged={Charged}",
                    deathId,
                    action,
                    requestId,
                    -existing.Delta);
                return Microsoft.AspNetCore.Http.Results.Ok(new
                {
                    success = true,
                    duplicate = true,
                    charged = -existing.Delta,
                    requestId
                });
            }

            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            try
            {
                var death = await db.DeathRecoveries.FirstOrDefaultAsync(x => x.DeathId == deathId, ct);
                if (death is null)
                {
                    await transaction.RollbackAsync(ct);
                    return Microsoft.AspNetCore.Http.Results.NotFound(new { success = false, reason = "death-not-found" });
                }
                if (death.PlayerUuid != request.PlayerUuid)
                {
                    await transaction.RollbackAsync(ct);
                    return Microsoft.AspNetCore.Http.Results.Conflict(new { success = false, reason = "player-mismatch" });
                }
                var selectedAction = (death.Action ?? string.Empty).Trim().ToLowerInvariant();
                if (string.IsNullOrWhiteSpace(selectedAction) || selectedAction != action)
                {
                    await transaction.RollbackAsync(ct);
                    logger.LogWarning(
                        "Minecraft death purchase rejected by single-choice guard: death={DeathId} selectedAction={SelectedAction} requestedAction={RequestedAction}",
                        deathId,
                        selectedAction,
                        action);
                    return Microsoft.AspNetCore.Http.Results.Conflict(new
                    {
                        success = false,
                        reason = string.IsNullOrWhiteSpace(selectedAction) ? "death-action-not-selected" : "death-action-already-selected",
                        selectedAction = string.IsNullOrWhiteSpace(selectedAction) ? null : selectedAction
                    });
                }
                if ((action is "chest" or "both") && !death.ChestSpotReserved)
                {
                    await transaction.RollbackAsync(ct);
                    return Microsoft.AspNetCore.Http.Results.Conflict(new { success = false, reason = "chest-place-not-reserved" });
                }

                var userId = death.UserId ?? await FindLinkedUserIdAsync(db, request.PlayerUuid, request.PlayerName, logger, ct);
                if (userId is null)
                {
                    await transaction.RollbackAsync(ct);
                    return Microsoft.AspNetCore.Http.Results.Conflict(new { success = false, reason = "not-linked" });
                }

                if (db.Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true)
                {
                    await db.Database.ExecuteSqlInterpolatedAsync(
                        $"SELECT pg_advisory_xact_lock(hashtextextended({userId.Value.ToString()}, 0))",
                        ct);
                }

                // Another purchase or compensation may have completed while this request was
                // waiting for the per-user advisory lock. Reload and repeat every state-sensitive
                // validation before charging rating.
                await db.Entry(death).ReloadAsync(ct);
                if (death.PlayerUuid != request.PlayerUuid)
                {
                    await transaction.RollbackAsync(ct);
                    return Microsoft.AspNetCore.Http.Results.Conflict(new { success = false, reason = "player-mismatch" });
                }
                if (death.UserId is Guid refreshedUserId && refreshedUserId != userId.Value)
                {
                    await transaction.RollbackAsync(ct);
                    return Microsoft.AspNetCore.Http.Results.Conflict(new { success = false, reason = "linked-user-mismatch" });
                }
                selectedAction = (death.Action ?? string.Empty).Trim().ToLowerInvariant();
                if (string.IsNullOrWhiteSpace(selectedAction) || selectedAction != action)
                {
                    await transaction.RollbackAsync(ct);
                    logger.LogWarning(
                        "Minecraft death purchase rejected after lock by single-choice guard: death={DeathId} selectedAction={SelectedAction} requestedAction={RequestedAction}",
                        deathId,
                        selectedAction,
                        action);
                    return Microsoft.AspNetCore.Http.Results.Conflict(new
                    {
                        success = false,
                        reason = string.IsNullOrWhiteSpace(selectedAction) ? "death-action-not-selected" : "death-action-already-selected",
                        selectedAction = string.IsNullOrWhiteSpace(selectedAction) ? null : selectedAction
                    });
                }
                if ((action is "chest" or "both") && !death.ChestSpotReserved)
                {
                    await transaction.RollbackAsync(ct);
                    return Microsoft.AspNetCore.Http.Results.Conflict(new { success = false, reason = "chest-place-not-reserved" });
                }

                if (await db.RatingTransactions.AsNoTracking().AnyAsync(x => x.RequestId == refundRequestId, ct))
                {
                    await transaction.RollbackAsync(ct);
                    logger.LogWarning(
                        "Minecraft death purchase blocked by compensation marker: death={DeathId} action={Action} request={RequestId}",
                        deathId,
                        action,
                        requestId);
                    return Microsoft.AspNetCore.Http.Results.Conflict(new
                    {
                        success = false,
                        reason = "purchase-already-compensated",
                        requestId
                    });
                }

                existing = await db.RatingTransactions.AsNoTracking()
                    .FirstOrDefaultAsync(x => x.RequestId == requestId, ct);
                if (existing is not null)
                {
                    if (!TransactionMatchesPurchase(existing, deathId, action, request.PlayerUuid))
                    {
                        await transaction.RollbackAsync(ct);
                        logger.LogError(
                            "Minecraft death purchase request-id collision after lock: death={DeathId} action={Action} request={RequestId} existingDelta={Delta}",
                            deathId,
                            action,
                            requestId,
                            existing.Delta);
                        return Microsoft.AspNetCore.Http.Results.Conflict(new { success = false, reason = "request-id-collision" });
                    }
                    var alreadyRefunded = await db.RatingTransactions.AsNoTracking()
                        .AnyAsync(x => x.RequestId == refundRequestId, ct);
                    if (alreadyRefunded)
                    {
                        await transaction.RollbackAsync(ct);
                        return Microsoft.AspNetCore.Http.Results.Conflict(new
                        {
                            success = false,
                            reason = "purchase-already-compensated",
                            requestId
                        });
                    }

                    await transaction.CommitAsync(ct);
                    return Microsoft.AspNetCore.Http.Results.Ok(new
                    {
                        success = true,
                        duplicate = true,
                        charged = -existing.Delta,
                        requestId,
                        revision = death.Revision
                    });
                }

                if (!await AreRatingBackendsHealthyAsync(cfg, httpFactory, ct))
                {
                    await transaction.RollbackAsync(ct);
                    return Microsoft.AspNetCore.Http.Results.Json(
                        new { success = false, reason = "rating-backend-unavailable" },
                        statusCode: StatusCodes.Status503ServiceUnavailable);
                }

                var ratingState = await TryLoadActivitySummaryStrictAsync(userId.Value, cfg, httpFactory, ct);
                if (!ratingState.Available)
                {
                    await transaction.RollbackAsync(ct);
                    return Microsoft.AspNetCore.Http.Results.Json(
                        new { success = false, reason = "rating-backend-unavailable" },
                        statusCode: StatusCodes.Status503ServiceUnavailable);
                }

                var adjustment = await db.RatingTransactions
                    .Where(x => x.UserId == userId.Value)
                    .SumAsync(x => (int?)x.Delta, ct) ?? 0;
                var baseRating = ratingState.Summary.Score > 0
                    ? ratingState.Summary.Score
                    : ratingState.Summary.Rating;
                var balance = Math.Max(0, baseRating + adjustment);
                if (balance < expectedAmount)
                {
                    logger.LogWarning(
                        "Minecraft death purchase denied for insufficient balance: user={UserId} death={DeathId} action={Action} cost={Cost} balance={Balance} request={RequestId}",
                        userId,
                        deathId,
                        action,
                        expectedAmount,
                        balance,
                        requestId);
                    await transaction.RollbackAsync(ct);
                    return Microsoft.AspNetCore.Http.Results.Conflict(new
                    {
                        success = false,
                        reason = "not-enough-rating",
                        cost = expectedAmount,
                        balance
                    });
                }

                var now = DateTimeOffset.UtcNow;
                db.RatingTransactions.Add(new MinecraftRatingTransaction
                {
                    Id = Guid.NewGuid(),
                    UserId = userId.Value,
                    PlayerName = string.IsNullOrWhiteSpace(request.PlayerName) ? death.PlayerName : Limit(request.PlayerName.Trim(), 120),
                    PlayerUuid = request.PlayerUuid.ToString(),
                    Delta = -expectedAmount,
                    Kind = action switch
                    {
                        "coordinates" => "death-coordinates",
                        "chest" => "death-chest",
                        "return" => "death-teleport",
                        _ => "death-chest-and-teleport"
                    },
                    Reason = action switch
                    {
                        "coordinates" => "Получение координат места смерти",
                        "chest" => "Сохранение вещей в сундуке после смерти",
                        "return" => "Возврат к месту смерти",
                        _ => "Сундук и возврат к месту смерти"
                    },
                    RequestId = requestId,
                    MetadataJson = JsonSerializer.Serialize(new { deathId, action }, JsonOptions()),
                    CreatedAtUtc = now
                });

                death.UserId = userId;
                death.Action = action;
                death.PurchaseRequestId = requestId;
                death.ChargedAmount = checked(death.ChargedAmount + expectedAmount);
                death.PaymentStatus = "confirmed";
                death.PaymentErrorCode = null;
                death.Compensated = false;
                death.CompensationPending = false;
                death.Revision = Math.Max(0, death.Revision) + 1;
                death.UpdatedAtUtc = now;
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);

                logger.LogInformation(
                    "Minecraft death purchase confirmed: user={UserId} player={PlayerUuid} death={DeathId} action={Action} amount={Amount} balanceBefore={BalanceBefore} balanceAfter={BalanceAfter} aggregateCharged={AggregateCharged} request={RequestId}",
                    userId,
                    request.PlayerUuid,
                    deathId,
                    action,
                    expectedAmount,
                    balance,
                    balance - expectedAmount,
                    death.ChargedAmount,
                    requestId);

                return Microsoft.AspNetCore.Http.Results.Ok(new
                {
                    success = true,
                    duplicate = false,
                    charged = expectedAmount,
                    newBalance = balance - expectedAmount,
                    requestId,
                    revision = death.Revision
                });
            }
            catch (DbUpdateException)
            {
                await transaction.RollbackAsync(ct);
                db.ChangeTracker.Clear();
                existing = await db.RatingTransactions.AsNoTracking()
                    .FirstOrDefaultAsync(x => x.RequestId == requestId, ct);
                if (existing is not null)
                {
                    if (!TransactionMatchesPurchase(existing, deathId, action, request.PlayerUuid))
                        return Microsoft.AspNetCore.Http.Results.Conflict(new { success = false, reason = "request-id-collision" });
                    return Microsoft.AspNetCore.Http.Results.Ok(new
                    {
                        success = true,
                        duplicate = true,
                        charged = -existing.Delta,
                        requestId
                    });
                }
                throw;
            }
        });

        app.MapPost(prefix + "/{deathId:guid}/compensate", async (
            Guid deathId,
            MinecraftDeathRecoveryCompensationRequest request,
            HttpContext http,
            IConfiguration cfg,
            MinecraftDbContext db,
            ILogger<Program> logger,
            CancellationToken ct) =>
        {
            if (!IsPluginAuthorized(http, cfg))
                return Microsoft.AspNetCore.Http.Results.Unauthorized();

            var sourceRequestId = (request.RequestId ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(sourceRequestId) || sourceRequestId.Length > 120 || request.Amount <= 0)
                return Microsoft.AspNetCore.Http.Results.BadRequest(new { success = false, reason = "bad-request" });

            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var death = await db.DeathRecoveries.FirstOrDefaultAsync(x => x.DeathId == deathId, ct);
            if (death is null)
            {
                await transaction.RollbackAsync(ct);
                return Microsoft.AspNetCore.Http.Results.NotFound(new { success = false, reason = "death-not-found" });
            }
            var source = await db.RatingTransactions.AsNoTracking()
                .FirstOrDefaultAsync(x => x.RequestId == sourceRequestId, ct);
            if (source is not null && !TransactionBelongsToDeath(source, deathId))
            {
                await transaction.RollbackAsync(ct);
                return Microsoft.AspNetCore.Http.Results.Conflict(new { success = false, reason = "purchase-mismatch" });
            }
            var userId = source?.UserId
                ?? death.UserId
                ?? await FindLinkedUserIdAsync(db, death.PlayerUuid, death.PlayerName, logger, ct);

            if (userId is Guid lockedUserId
                && db.Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true)
            {
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT pg_advisory_xact_lock(hashtextextended({lockedUserId.ToString()}, 0))",
                    ct);
            }

            // Re-read after the per-user lock. If an ambiguous purchase was still committing,
            // this sees it and refunds it; otherwise the no-charge marker prevents a late charge.
            await db.Entry(death).ReloadAsync(ct);
            source = await db.RatingTransactions.AsNoTracking()
                .FirstOrDefaultAsync(x => x.RequestId == sourceRequestId, ct);
            if (source is not null && !TransactionBelongsToDeath(source, deathId))
            {
                await transaction.RollbackAsync(ct);
                return Microsoft.AspNetCore.Http.Results.Conflict(new { success = false, reason = "purchase-mismatch" });
            }
            var refundRequestId = "death-refund:" + sourceRequestId;
            var existingRefund = await db.RatingTransactions.AsNoTracking()
                .FirstOrDefaultAsync(x => x.RequestId == refundRequestId, ct);

            if (existingRefund is not null)
            {
                death.PaymentStatus = death.ChargedAmount > 0 ? "confirmed" : "compensated";
                death.CompensationPending = false;
                death.Compensated = death.ChargedAmount == 0;
                death.Revision = Math.Max(0, death.Revision) + 1;
                death.UpdatedAtUtc = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                return Microsoft.AspNetCore.Http.Results.Ok(new
                {
                    success = true,
                    duplicate = true,
                    noCharge = existingRefund.Delta == 0,
                    refunded = existingRefund.Delta,
                    revision = death.Revision
                });
            }

            if (source is null)
            {
                if (userId is not Guid markerUserId || markerUserId == Guid.Empty)
                {
                    await transaction.RollbackAsync(ct);
                    return Microsoft.AspNetCore.Http.Results.Conflict(new { success = false, reason = "linked-user-missing" });
                }
                db.RatingTransactions.Add(new MinecraftRatingTransaction
                {
                    Id = Guid.NewGuid(),
                    UserId = markerUserId,
                    PlayerName = death.PlayerName,
                    PlayerUuid = death.PlayerUuid.ToString(),
                    Delta = 0,
                    Kind = "death-purchase-refund-marker",
                    Reason = "Запрет позднего списания после неоднозначной Minecraft-операции",
                    RequestId = refundRequestId,
                    MetadataJson = JsonSerializer.Serialize(new { deathId, sourceRequestId, noCharge = true }, JsonOptions()),
                    CreatedAtUtc = DateTimeOffset.UtcNow
                });
                death.UserId ??= markerUserId;
                death.PaymentStatus = death.ChargedAmount > 0 ? "confirmed" : "compensated";
                death.PaymentErrorCode = "no-charge-to-refund";
                death.CompensationPending = false;
                death.Compensated = death.ChargedAmount == 0;
                death.Revision = Math.Max(0, death.Revision) + 1;
                death.UpdatedAtUtc = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);

                logger.LogInformation(
                    "Minecraft ambiguous death purchase reconciled without charge: death={DeathId} request={RequestId}",
                    deathId,
                    sourceRequestId);
                return Microsoft.AspNetCore.Http.Results.Ok(new
                {
                    success = true,
                    duplicate = false,
                    noCharge = true,
                    refunded = 0,
                    revision = death.Revision
                });
            }

            var chargedAmount = -source.Delta;
            if (source.Delta >= 0 || chargedAmount != request.Amount || (userId is Guid expectedUserId && source.UserId != expectedUserId))
            {
                await transaction.RollbackAsync(ct);
                return Microsoft.AspNetCore.Http.Results.Conflict(new { success = false, reason = "purchase-mismatch" });
            }

            db.RatingTransactions.Add(new MinecraftRatingTransaction
            {
                Id = Guid.NewGuid(),
                UserId = source.UserId,
                PlayerName = death.PlayerName,
                PlayerUuid = death.PlayerUuid.ToString(),
                Delta = chargedAmount,
                Kind = "death-purchase-refund",
                Reason = Limit(string.IsNullOrWhiteSpace(request.Reason)
                    ? "Возврат за невыполненную Minecraft-операцию"
                    : request.Reason.Trim(), 500),
                RequestId = refundRequestId,
                MetadataJson = JsonSerializer.Serialize(new { deathId, sourceRequestId }, JsonOptions()),
                CreatedAtUtc = DateTimeOffset.UtcNow
            });
            death.UserId = source.UserId;
            death.ChargedAmount = Math.Max(0, death.ChargedAmount - chargedAmount);
            death.PaymentStatus = death.ChargedAmount > 0 ? "confirmed" : "compensated";
            death.PaymentErrorCode = null;
            death.CompensationPending = false;
            death.Compensated = death.ChargedAmount == 0;
            death.Revision = Math.Max(0, death.Revision) + 1;
            death.UpdatedAtUtc = DateTimeOffset.UtcNow;

            try
            {
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
            }
            catch (DbUpdateException)
            {
                await transaction.RollbackAsync(ct);
                db.ChangeTracker.Clear();
                existingRefund = await db.RatingTransactions.AsNoTracking()
                    .FirstOrDefaultAsync(x => x.RequestId == refundRequestId, ct);
                if (existingRefund is not null)
                    return Microsoft.AspNetCore.Http.Results.Ok(new { success = true, duplicate = true, noCharge = false, refunded = existingRefund.Delta });
                throw;
            }

            logger.LogWarning(
                "Minecraft death purchase compensated: user={UserId} death={DeathId} sourceRequest={SourceRequestId} refundRequest={RefundRequestId} amount={Amount} aggregateChargedRemaining={AggregateChargedRemaining}",
                source.UserId,
                deathId,
                sourceRequestId,
                refundRequestId,
                chargedAmount,
                death.ChargedAmount);
            return Microsoft.AspNetCore.Http.Results.Ok(new
            {
                success = true,
                duplicate = false,
                noCharge = false,
                refunded = chargedAmount,
                revision = death.Revision
            });
        });

        return app;
    }


    private static bool TransactionMatchesPurchase(
        MinecraftRatingTransaction transaction,
        Guid deathId,
        string action,
        Guid playerUuid)
    {
        if (transaction.Delta >= 0) return false;
        if (!string.Equals(transaction.PlayerUuid, playerUuid.ToString(), StringComparison.OrdinalIgnoreCase)) return false;
        if (string.IsNullOrWhiteSpace(transaction.MetadataJson)) return false;
        try
        {
            using var document = JsonDocument.Parse(transaction.MetadataJson);
            var root = document.RootElement;
            if (!root.TryGetProperty("deathId", out var deathValue)
                || deathValue.ValueKind != JsonValueKind.String
                || !Guid.TryParse(deathValue.GetString(), out var parsedDeath)
                || parsedDeath != deathId)
            {
                return false;
            }
            return root.TryGetProperty("action", out var actionValue)
                && actionValue.ValueKind == JsonValueKind.String
                && string.Equals(actionValue.GetString(), action, StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TransactionBelongsToDeath(MinecraftRatingTransaction transaction, Guid deathId)
    {
        if (string.IsNullOrWhiteSpace(transaction.MetadataJson)) return false;
        try
        {
            using var document = JsonDocument.Parse(transaction.MetadataJson);
            if (!document.RootElement.TryGetProperty("deathId", out var value)) return false;
            return value.ValueKind == JsonValueKind.String
                && Guid.TryParse(value.GetString(), out var parsed)
                && parsed == deathId;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static async Task<Guid?> FindLinkedUserIdAsync(
        MinecraftDbContext db,
        Guid playerUuid,
        string? playerName,
        ILogger<Program> logger,
        CancellationToken ct)
    {
        var uuid = playerUuid.ToString().ToLowerInvariant();
        var uuidOwners = await db.Links.AsNoTracking()
            .Where(x => x.Confirmed
                && x.UnlinkedAtUtc == null
                && x.UserId != null
                && x.PlayerUuid != null
                && x.PlayerUuid.ToLower() == uuid)
            .Select(x => x.UserId!.Value)
            .Distinct()
            .Take(2)
            .ToListAsync(ct);
        if (uuidOwners.Count == 1) return uuidOwners[0];
        if (uuidOwners.Count > 1)
        {
            logger.LogCritical(
                "Death recovery refused ambiguous Minecraft UUID ownership to protect the shared balance: playerUuid={PlayerUuid} userIds={UserIds}",
                playerUuid,
                string.Join(',', uuidOwners));
            return null;
        }

        var nick = NormalizeNick(playerName);
        if (!IsValidNick(nick)) return null;
        var lower = nick.ToLowerInvariant();
        var legacyCandidates = await db.Links.AsNoTracking()
            .Where(x => x.Confirmed
                && x.UnlinkedAtUtc == null
                && x.UserId != null
                && (x.PlayerUuid == null || x.PlayerUuid == string.Empty)
                && x.PlayerName != null
                && x.PlayerName.ToLower() == lower)
            .Select(x => x.UserId!.Value)
            .Distinct()
            .Take(2)
            .ToListAsync(ct);
        if (legacyCandidates.Count == 1) return legacyCandidates[0];
        if (legacyCandidates.Count > 1)
        {
            logger.LogCritical(
                "Death recovery refused ambiguous UUID-less nickname ownership to protect the shared balance: playerName={PlayerName} userIds={UserIds}",
                nick,
                string.Join(',', legacyCandidates));
        }
        return null;
    }

    private static object ToDeathRecoveryDto(MinecraftDeathRecovery x) => new
    {
        deathId = x.DeathId,
        playerUuid = x.PlayerUuid,
        playerName = x.PlayerName,
        worldUuid = x.WorldUuid,
        worldKey = x.WorldKey,
        worldName = x.WorldName,
        x = x.X,
        y = x.Y,
        z = x.Z,
        yaw = x.Yaw,
        pitch = x.Pitch,
        itemsPayload = x.ItemsPayload,
        createdAtUtc = x.CreatedAtUtc,
        offerExpiresAtUtc = x.OfferExpiresAtUtc,
        rescueEndsAtUtc = x.RescueEndsAtUtc,
        stage = x.Stage,
        action = x.Action,
        purchaseRequestId = x.PurchaseRequestId,
        chargedAmount = x.ChargedAmount,
        paymentStatus = x.PaymentStatus,
        paymentErrorCode = x.PaymentErrorCode,
        dropsReleased = x.DropsReleased,
        chestSpotReserved = x.ChestSpotReserved,
        chestCreated = x.ChestCreated,
        itemsResolved = x.ItemsResolved,
        rescuePending = x.RescuePending,
        rescueCompleted = x.RescueCompleted,
        previousGameMode = x.PreviousGameMode,
        backendUnavailable = x.BackendUnavailable,
        compensationPending = x.CompensationPending,
        compensated = x.Compensated,
        chestX = x.ChestX,
        chestY = x.ChestY,
        chestZ = x.ChestZ,
        chestSecondX = x.ChestSecondX,
        chestSecondY = x.ChestSecondY,
        chestSecondZ = x.ChestSecondZ,
        finalX = x.FinalX,
        finalY = x.FinalY,
        finalZ = x.FinalZ,
        lastError = x.LastError,
        revision = x.Revision,
        updatedAtUtc = x.UpdatedAtUtc
    };

    private static int PaymentStatusRank(string? status)
        => (status ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "pending" => 1,
            "confirmed" => 2,
            "compensated" => 3,
            _ => 0
        };

    private static bool IsTerminalDeathStage(string? stage)
        => (stage ?? string.Empty).Trim().ToUpperInvariant() is
            "DROPS_RELEASED" or
            "CHEST_CREATED" or
            "FREE_CHEST_CREATED" or
            "COORDINATES_SENT" or
            "RESCUE_COMPLETED" or
            "CHEST_AND_RESCUE_COMPLETED";

    private static string Limit(string value, int max)
        => value.Length <= max ? value : value[..max];

    private static string? NullIfBlank(string? value, int max)
    {
        var text = (value ?? string.Empty).Trim();
        return text.Length == 0 ? null : Limit(text, max);
    }
}
