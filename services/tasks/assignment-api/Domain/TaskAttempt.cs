namespace TaskForge.Tasks.Api.Domain;

public sealed class TaskAttempt
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Kind { get; set; } = "test"; // test | math
    public Guid TaskAssignmentId { get; set; }
    public Guid UserId { get; set; }
    public int AttemptNumber { get; set; }
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public int? TimeLimitSeconds { get; set; }
    public bool TimeExpired { get; set; }
    public DateTimeOffset? SubmittedAt { get; set; }
    public int ScorePercent { get; set; }
    public bool Passed { get; set; }
    public int TotalUnits { get; set; }
    public int CorrectUnits { get; set; }
    public int TotalScore { get; set; }
    public int EarnedScore { get; set; }
    public string? OrderJson { get; set; }
    public string? AnswersJson { get; set; }
    public string? ReviewJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
