using System.ComponentModel.DataAnnotations;

namespace taskforge.Data.Models.Entities.AI;

public sealed class AiDecisionLog
{
    [Key]
    public Guid Id { get; set; }

    public Guid? BatchId { get; set; }
    public AiBatch? Batch { get; set; }

    public Guid? BatchItemId { get; set; }
    public AiBatchItem? BatchItem { get; set; }

    public Guid? JobId { get; set; }
    public AiJob? Job { get; set; }

    [Required, MaxLength(80)]
    public string StageCode { get; set; } = string.Empty;

    [Required, MaxLength(120)]
    public string DecisionType { get; set; } = string.Empty;

    [Required]
    public string Message { get; set; } = string.Empty;

    public string? PayloadJson { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
