namespace TaskForge.Minecraft.Api.Contracts;

public sealed record MinecraftLinkRequest(string? Nick);

public sealed record MinecraftConfirmRequest(string? Code, string? PlayerName = null, string? PlayerUuid = null);

public sealed record MinecraftChatRequest(string? Author, string? Text, string? Message = null);

public sealed record IncomingMinecraftChatRequest(string? Nick, string? Uuid, string? Message, string? Kind = null);

public sealed record MinecraftJoinEventRequest(string? Nick, string? Uuid);

public sealed record UserIdsRequest(Guid[] UserIds);

public sealed record RoleAssignRequest(string? Code);


public sealed record MinecraftRatingRestoreRequest(int Amount, string? Reason);

public sealed record MinecraftDeathRecoveryStateRequest(
    Guid DeathId,
    Guid PlayerUuid,
    string? PlayerName,
    Guid WorldUuid,
    string? WorldKey,
    string? WorldName,
    double X,
    double Y,
    double Z,
    float Yaw,
    float Pitch,
    string? ItemsPayload,
    DateTimeOffset? CreatedAtUtc,
    DateTimeOffset? OfferExpiresAtUtc,
    DateTimeOffset? RescueEndsAtUtc,
    string? Stage,
    string? Action,
    string? PurchaseRequestId,
    int ChargedAmount,
    string? PaymentStatus,
    string? PaymentErrorCode,
    bool DropsReleased,
    bool ChestSpotReserved,
    bool ChestCreated,
    bool ItemsResolved,
    bool RescuePending,
    bool RescueCompleted,
    string? PreviousGameMode,
    bool BackendUnavailable,
    bool CompensationPending,
    bool Compensated,
    int? ChestX,
    int? ChestY,
    int? ChestZ,
    int? ChestSecondX,
    int? ChestSecondY,
    int? ChestSecondZ,
    double? FinalX,
    double? FinalY,
    double? FinalZ,
    string? LastError,
    long Revision);

public sealed record MinecraftDeathRecoveryPurchaseRequest(
    Guid PlayerUuid,
    string? PlayerName,
    string? RequestId,
    string? Action,
    int Amount);

public sealed record MinecraftDeathRecoveryCompensationRequest(
    string? RequestId,
    int Amount,
    string? Reason);
