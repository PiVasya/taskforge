namespace TaskForge.Solutions.Api.Domain;

public sealed class SolutionSubmission
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AssignmentId { get; set; }
    public Guid? UserId { get; set; }
    // Null for historical submissions; SQL attempts pin authoritative tasks-api revisions.
    public string? ExecutionTarget { get; set; }
    public Guid? SqlSpecVersionId { get; set; }
    public Guid? SqlEngineProfileId { get; set; }
    public string Language { get; set; } = "csharp";
    public string Code { get; set; } = string.Empty;
    public string Status { get; set; } = "Preparing";
    public int Score { get; set; } = 0;
    public string? ResultJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
