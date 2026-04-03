using System.ComponentModel.DataAnnotations;

namespace taskforge.Data.Models.Entities.AI;

public sealed class AiBatchItem
{
    [Key]
    public Guid Id { get; set; }

    [Required]
    public Guid BatchId { get; set; }
    public AiBatch Batch { get; set; } = null!;

    public int Index { get; set; }

    [MaxLength(120)]
    public string? TargetSkill { get; set; }

    public int DifficultyTarget { get; set; } = 2;

    public string? MicroGoal { get; set; }

    [Required, MaxLength(32)]
    public string Status { get; set; } = "pending";

    public string? BriefJson { get; set; }
    public string? BriefReviewJson { get; set; }
    public string? ContextReviewJson { get; set; }
    public string? ReferencePackJson { get; set; }
    public string? StylePackJson { get; set; }
    public string? PolicyPackJson { get; set; }
    public string? NegativePackJson { get; set; }
    public string? ExemplarPackJson { get; set; }
    public string? ReferenceSignalsJson { get; set; }
    public string? DecisionLogJson { get; set; }
    public string? PlannerSignalsJson { get; set; }
    public string? HistoricalSlotPriorsJson { get; set; }
    public string? AntiPatternFlagsJson { get; set; }
    public string? ReplanHistoryJson { get; set; }
    public Guid? DraftId { get; set; }
    public AiGeneratedAssignmentDraft? Draft { get; set; }
    public string? ScorecardJson { get; set; }
    public int RepairCount { get; set; } = 0;
    public Guid? ParentItemId { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    public ICollection<AiDecisionLog> DecisionLogs { get; set; } = new List<AiDecisionLog>();
    public ICollection<AiReferenceSnapshot> ReferenceSnapshots { get; set; } = new List<AiReferenceSnapshot>();
}
