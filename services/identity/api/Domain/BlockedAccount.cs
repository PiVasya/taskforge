namespace TaskForge.Identity.Api.Domain;

public sealed class BlockedAccount
{
    public Guid UserId { get; set; }
    public string Reason { get; set; } = "manual";
    public string? Note { get; set; }
    public Guid BlockedByUserId { get; set; }
    public DateTimeOffset BlockedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ExpiresAtUtc { get; set; }
}
