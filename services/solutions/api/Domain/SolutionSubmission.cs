namespace TaskForge.Solutions.Api.Domain;

public sealed class SolutionSubmission
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AssignmentId { get; set; }
    public Guid? UserId { get; set; }
    public string Language { get; set; } = "csharp";
    public string Code { get; set; } = string.Empty;
    public string Status { get; set; } = "Accepted";
    public int Score { get; set; } = 100;
    public string? ResultJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
