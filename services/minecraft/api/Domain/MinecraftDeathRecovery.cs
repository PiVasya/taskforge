using System.ComponentModel.DataAnnotations;

namespace TaskForge.Minecraft.Api.Domain;

public sealed class MinecraftDeathRecovery
{
    public Guid DeathId { get; set; }
    public Guid? UserId { get; set; }
    public Guid PlayerUuid { get; set; }

    [MaxLength(32)]
    public string PlayerName { get; set; } = string.Empty;

    public Guid WorldUuid { get; set; }

    [MaxLength(160)]
    public string WorldKey { get; set; } = string.Empty;

    [MaxLength(160)]
    public string WorldName { get; set; } = string.Empty;

    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    public float Yaw { get; set; }
    public float Pitch { get; set; }

    public string ItemsPayload { get; set; } = string.Empty;

    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset OfferExpiresAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }

    [MaxLength(64)]
    public string Stage { get; set; } = "WAITING_RESPAWN";

    [MaxLength(32)]
    public string? Action { get; set; }

    [MaxLength(120)]
    public string? PurchaseRequestId { get; set; }

    public int ChargedAmount { get; set; }

    [MaxLength(32)]
    public string PaymentStatus { get; set; } = "none";

    [MaxLength(128)]
    public string? PaymentErrorCode { get; set; }

    public bool DropsReleased { get; set; }
    public bool ChestSpotReserved { get; set; }
    public bool ChestCreated { get; set; }
    public bool ItemsResolved { get; set; }
    public bool RescuePending { get; set; }
    public bool RescueCompleted { get; set; }

    [MaxLength(32)]
    public string? PreviousGameMode { get; set; }

    public DateTimeOffset? RescueEndsAtUtc { get; set; }

    public int? ChestX { get; set; }
    public int? ChestY { get; set; }
    public int? ChestZ { get; set; }
    public int? ChestSecondX { get; set; }
    public int? ChestSecondY { get; set; }
    public int? ChestSecondZ { get; set; }

    public double? FinalX { get; set; }
    public double? FinalY { get; set; }
    public double? FinalZ { get; set; }

    public bool BackendUnavailable { get; set; }
    public bool CompensationPending { get; set; }
    public bool Compensated { get; set; }

    [MaxLength(1024)]
    public string? LastError { get; set; }

    public long Revision { get; set; }
}
