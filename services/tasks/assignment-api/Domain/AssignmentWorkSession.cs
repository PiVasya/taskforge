namespace TaskForge.Tasks.Api.Domain;

public sealed class AssignmentWorkSession
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AssignmentId { get; set; }
    public Guid UserId { get; set; }
    public string SessionId { get; set; } = string.Empty;
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastActivityAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAt { get; set; }
    public long TotalDurationMs { get; set; }
    public long ActiveDurationMs { get; set; }
    public long HiddenDurationMs { get; set; }
    public long BlurDurationMs { get; set; }
    public int OpenCount { get; set; }
    public int CloseCount { get; set; }
    public int HiddenCount { get; set; }
    public int VisibleCount { get; set; }
    public int BlurCount { get; set; }
    public int FocusCount { get; set; }
    public int PasteCount { get; set; }
    public int CopyCount { get; set; }
    public int CutCount { get; set; }
    public int CodeChangeCount { get; set; }
    public int LanguageChangeCount { get; set; }
    public int SubmitCount { get; set; }
    public int FailedSubmitCount { get; set; }
    public int PassedSubmitCount { get; set; }
    public int FullscreenExitCount { get; set; }
    public int MaxCodeLength { get; set; }
    public int FinalCodeLength { get; set; }
    public string? FinalCodeHash { get; set; }
    public string? LastLanguage { get; set; }
    public int RiskScore { get; set; }
    public string RiskLevel { get; set; } = "low";
    public string? RiskReasonsJson { get; set; }
    public string? LastEventType { get; set; }
}
