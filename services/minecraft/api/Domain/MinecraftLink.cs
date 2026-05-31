namespace TaskForge.Minecraft.Api.Domain;

public sealed class MinecraftLink
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? UserId { get; set; }
    public string? PlayerName { get; set; }
    public string? PlayerUuid { get; set; }
    public string Code { get; set; } = string.Empty;
    public bool Confirmed { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
