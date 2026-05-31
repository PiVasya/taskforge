namespace TaskForge.Solutions.Api.Domain;

public sealed class UserRating
{
    public Guid UserId { get; set; }
    public int TotalScore { get; set; }
    public int SolvedCount { get; set; }
    public int AttemptsCount { get; set; }
    public int AcceptedCount { get; set; }
    public int RejectedCount { get; set; }
    public DateTimeOffset? LastAcceptedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class LeaderboardEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public string Scope { get; set; } = "global";
    public string? CourseId { get; set; }
    public string? GroupId { get; set; }
    public int Rank { get; set; }
    public int Score { get; set; }
    public int SolvedCount { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class RatingProjectionCheckpoint
{
    public string ProjectionName { get; set; } = string.Empty;
    public long LastEventSequence { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed record SolutionVerdictChangedEvent(
    Guid SubmissionId,
    Guid UserId,
    Guid TaskId,
    string Verdict,
    int ScoreDelta,
    bool IsAccepted,
    DateTimeOffset OccurredAt);
