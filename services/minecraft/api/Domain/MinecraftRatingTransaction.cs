namespace TaskForge.Minecraft.Api.Domain;

public sealed class MinecraftRatingTransaction
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public string? PlayerName { get; set; }
    public string? PlayerUuid { get; set; }
    public int Delta { get; set; }
    public string Kind { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public string? RequestId { get; set; }
    public string? MetadataJson { get; set; }
    public Guid? ActorUserId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
