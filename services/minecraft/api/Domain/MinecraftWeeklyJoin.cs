namespace TaskForge.Minecraft.Api.Domain;

public sealed class MinecraftWeeklyJoin
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public DateTimeOffset WeekStartUtc { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public int PenaltyApplied { get; set; }
}
