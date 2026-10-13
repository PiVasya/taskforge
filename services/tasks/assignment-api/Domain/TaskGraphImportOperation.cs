namespace TaskForge.Tasks.Api.Domain;

public sealed class TaskGraphImportOperation
{
    public Guid Id { get; set; }
    public Guid CourseId { get; set; }
    public Guid UserId { get; set; }
    public string RequestHash { get; set; } = "";
    public string PreparedPayloadJson { get; set; } = "{}";
    public string State { get; set; } = "prepared";
    public string? ResponseJson { get; set; }
    public string? MapRequestJson { get; set; }
    public string? MapResponseJson { get; set; }
    public string? LastErrorJson { get; set; }
    public string AuditJson { get; set; } = "{}";
    public int AttemptCount { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
