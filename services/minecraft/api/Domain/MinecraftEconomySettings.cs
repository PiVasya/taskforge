namespace TaskForge.Minecraft.Api.Domain;

public sealed class MinecraftEconomySettings
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public int WeeklyPenalty { get; set; } = 70;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
