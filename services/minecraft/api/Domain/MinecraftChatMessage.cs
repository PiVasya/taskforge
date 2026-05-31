namespace TaskForge.Minecraft.Api.Domain;

public sealed class MinecraftChatMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Author { get; set; } = "web";
    public string Text { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
