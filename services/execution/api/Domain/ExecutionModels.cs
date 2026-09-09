namespace TaskForge.Execution.Api.Domain;

public sealed class ExecutionJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    // Preview and materialization jobs do not create a graded submission.
    public Guid? SubmissionId { get; set; }
    public string Kind { get; set; } = ExecutionJobKinds.Legacy;
    public string? Target { get; set; }
    public int? PayloadVersion { get; set; }
    public string? PayloadJson { get; set; }
    // Computed internally from the producer's operation identity, not a global client key.
    public string? DeduplicationKey { get; set; }
    public string? ClaimedByWorkerId { get; set; }
    public Guid? LeaseToken { get; set; }
    public DateTimeOffset? LeaseExpiresAt { get; set; }
    public Guid? AssignmentId { get; set; }
    public Guid? UserId { get; set; }
    public string Language { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public string? Input { get; set; }
    public string? TestsJson { get; set; }
    public string? CodeForbiddenCallsJson { get; set; }
    public string? CodeRequiredCallsJson { get; set; }
    public int? TimeLimitMs { get; set; }
    public int? MemoryLimitMb { get; set; }
    public int AttemptCount { get; set; }
    public string Status { get; set; } = "queued";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

public sealed class ExecutionResult
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid JobId { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? Stdout { get; set; }
    public string? Stderr { get; set; }
    public string? ResultJson { get; set; }
    public int? ExitCode { get; set; }
    public int Score { get; set; }
    public bool Passed { get; set; }
    public long DurationMs { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class RunnerHeartbeat
{
    public string RunnerId { get; set; } = string.Empty;
    public string Language { get; set; } = string.Empty;
    public string Region { get; set; } = string.Empty;
    public DateTimeOffset LastSeenAt { get; set; } = DateTimeOffset.UtcNow;
}
