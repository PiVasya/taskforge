namespace TaskForge.Identity.Api.Domain;

public sealed class UserUiSettings
{
    public Guid UserId { get; set; }
    public string DataJson { get; set; } = "{}";
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
