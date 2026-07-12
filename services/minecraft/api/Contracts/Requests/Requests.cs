namespace TaskForge.Minecraft.Api.Contracts;

public sealed record MinecraftLinkRequest(string? Nick);

public sealed record MinecraftConfirmRequest(string? Code, string? PlayerName = null, string? PlayerUuid = null);

public sealed record MinecraftChatRequest(string? Author, string? Text, string? Message = null);

public sealed record IncomingMinecraftChatRequest(string? Nick, string? Uuid, string? Message, string? Kind = null);

public sealed record MinecraftEconomyUpdateRequest(int WeeklyPenalty);

public sealed record MinecraftJoinEventRequest(string? Nick, string? Uuid);

public sealed record UserIdsRequest(Guid[] UserIds);

public sealed record RoleAssignRequest(string? Code);

public sealed record MinecraftDeathTeleportQuoteRequest(string? Nick, string? Uuid, string? DeathId);

public sealed record MinecraftDeathTeleportPurchaseRequest(string? Nick, string? Uuid, string? DeathId, string? RequestId);

public sealed record MinecraftRatingRestoreRequest(int Amount, string? Reason);
