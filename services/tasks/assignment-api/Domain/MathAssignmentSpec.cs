namespace TaskForge.Tasks.Api.Domain;

public sealed class MathAssignmentSpec
{
    public Guid AssignmentId { get; set; }
    public string SettingsJson { get; set; } = "{}";
    public string BlocksJson { get; set; } = "[]";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
