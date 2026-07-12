namespace TaskForge.Minecraft.Api.Domain;

public sealed class MinecraftLinkCode
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public string Nick { get; set; } = string.Empty;
    public byte[] CodeHash { get; set; } = Array.Empty<byte>();
    public byte[] Salt { get; set; } = Array.Empty<byte>();
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UsedAtUtc { get; set; }
}
