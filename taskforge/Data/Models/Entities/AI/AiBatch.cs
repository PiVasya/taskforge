using System.ComponentModel.DataAnnotations;

namespace taskforge.Data.Models.Entities.AI;

public sealed class AiBatch
{
    [Key]
    public Guid Id { get; set; }

    public Guid? CourseId { get; set; }
    public Course? Course { get; set; }

    public Guid? CreatedByUserId { get; set; }
    public User? CreatedByUser { get; set; }

    /// <summary>Chat session that created this batch (for feedback loop).</summary>
    public Guid? ChatSessionId { get; set; }

    [Required]
    public string Prompt { get; set; } = string.Empty;

    [Required, MaxLength(50)]
    public string AssignmentType { get; set; } = "math";

    [MaxLength(80)]
    public string Mode { get; set; } = "topic-pack";

    public int RequestedCount { get; set; } = 1;

    [Required, MaxLength(32)]
    public string Status { get; set; } = "pending";

    [MaxLength(80)]
    public string? CurrentStage { get; set; }

    public string? CanonicalRequestJson { get; set; }
    public string? CourseProfileJson { get; set; }
    public string? GapAnalysisJson { get; set; }
    public string? AssignmentOntologyJson { get; set; }
    public string? ExemplarSignalsJson { get; set; }
    public string? NegativeMemoryJson { get; set; }
    public string? CoverageJson { get; set; }
    public string? PlanJson { get; set; }
    public string? SummaryJson { get; set; }
    public string? DecisionSummaryJson { get; set; }
    public string? ReviewLedgerJson { get; set; }
    public string? BatchReviewJson { get; set; }
    public string? StudentJourneyJson { get; set; }
    public string? PublicationAuditJson { get; set; }
    public string? PublishPackJson { get; set; }
    public string? QualityLedgerJson { get; set; }
    public string? ExportManifestJson { get; set; }
    public string? PlannerFeedbackJson { get; set; }
    public string? HistoricalPlannerPriorsJson { get; set; }
    public string? PositiveMemoryJson { get; set; }
    public string? BatchMemoryJson { get; set; }
    public string? InstitutionalMemoryJson { get; set; }
    public string? AntiPatternMemoryJson { get; set; }
    public string? ReplanLedgerJson { get; set; }
    public string? DecisionLogDigestJson { get; set; }
    public string? FeedbackLoopStateJson { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    public ICollection<AiBatchItem> Items { get; set; } = new List<AiBatchItem>();
    public ICollection<AiDecisionLog> DecisionLogs { get; set; } = new List<AiDecisionLog>();
    public ICollection<AiReferenceSnapshot> ReferenceSnapshots { get; set; } = new List<AiReferenceSnapshot>();
}
