namespace TaskForge.Ai.Api.Domain;

public sealed class AccountAnalysisRun
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RequestedByUserId { get; set; }
    public string Status { get; set; } = "queued";
    public string Phase { get; set; } = "queued";
    public int ProgressPercent { get; set; }
    public int TotalAccounts { get; set; }
    public int CandidatePairs { get; set; }
    public int DuplicateFindings { get; set; }
    public int SuspiciousFindings { get; set; }
    public string AlgorithmVersion { get; set; } = "account-intelligence-v1.1";
    public string? SourcesJson { get; set; }
    public string? ErrorJson { get; set; }
    public DateTimeOffset? StartedAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class AccountAnalysisFinding
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RunId { get; set; }
    public string FindingKey { get; set; } = string.Empty;
    public string Kind { get; set; } = "duplicate";
    public string Status { get; set; } = "open";
    public int Score { get; set; }
    public double ModelProbability { get; set; }
    public Guid PrimaryUserId { get; set; }
    public Guid? SecondaryUserId { get; set; }
    public Guid? SuggestedPrimaryUserId { get; set; }
    public string DataJson { get; set; } = "{}";
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class AccountAnalysisReview
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string SubjectType { get; set; } = "pair";
    public string SubjectKey { get; set; } = string.Empty;
    public Guid? UserId { get; set; }
    public Guid? OtherUserId { get; set; }
    public string Decision { get; set; } = "reviewed";
    public string? Note { get; set; }
    public string? SignalsJson { get; set; }
    public string? DataJson { get; set; }
    public Guid ReviewedByUserId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
