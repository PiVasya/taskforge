namespace TaskForge.Education.Api.Domain;

public sealed class GroupMember
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid GroupId { get; set; }
    public Guid UserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
