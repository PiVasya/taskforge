namespace TaskForge.Tasks.Api.Domain;

public sealed class AssignmentActivityEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AssignmentId { get; set; }
    public Guid UserId { get; set; }
    public string SessionId { get; set; } = string.Empty;
    public string? EventUid { get; set; }
    public int Sequence { get; set; }
    public string EventType { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ClientTime { get; set; }
    public string? PayloadJson { get; set; }
    public int? CodeLength { get; set; }
    public int? CodeDelta { get; set; }
    public string? CodeHash { get; set; }
    public int? TextLength { get; set; }
    public string? TextHash { get; set; }
    public string? TextSample { get; set; }
    public string? Language { get; set; }
    public long? ActiveDurationMs { get; set; }
    public long? HiddenDurationMs { get; set; }
    public long? BlurDurationMs { get; set; }
    public Guid? AttemptId { get; set; }
    public Guid? SubmissionId { get; set; }
    public int RiskPoints { get; set; }
    public string? RiskReason { get; set; }
    public string? IpHash { get; set; }
    public string? UserAgentHash { get; set; }
}
