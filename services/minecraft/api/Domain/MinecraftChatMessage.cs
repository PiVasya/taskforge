namespace TaskForge.Minecraft.Api.Domain;

public sealed class MinecraftChatMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? UserId { get; set; }
    public string Source { get; set; } = "SiteUser";
    public string? AuthorName { get; set; }
    public string? MinecraftNick { get; set; }
    public string? MinecraftUuid { get; set; }
    public string Message { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
