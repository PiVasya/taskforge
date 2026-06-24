namespace TaskForge.Tasks.Api.Domain;

public sealed class AssignmentCodeSnapshot
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AssignmentId { get; set; }
    public Guid UserId { get; set; }
    public string SessionId { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string Source { get; set; } = "unknown";
    public string? Language { get; set; }
    public int CodeLength { get; set; }
    public int? CodeDelta { get; set; }
    public string? CodeHash { get; set; }
    public string? CodeSample { get; set; }
    public string? FullCode { get; set; }
    public Guid? RelatedEventId { get; set; }
    public Guid? RelatedSubmissionId { get; set; }
}
