using System.ComponentModel.DataAnnotations;

namespace taskforge.Data.Models.Entities.AI;

public sealed class AiReferenceSnapshot
{
    [Key]
    public Guid Id { get; set; }

    public Guid? BatchId { get; set; }
    public AiBatch? Batch { get; set; }

    public Guid? BatchItemId { get; set; }
    public AiBatchItem? BatchItem { get; set; }

    public Guid? JobId { get; set; }
    public AiJob? Job { get; set; }

    public Guid? SourceCourseId { get; set; }

    [MaxLength(120)]
    public string? SourceAssignmentExternalId { get; set; }

    [Required, MaxLength(80)]
    public string Role { get; set; } = string.Empty;

    public string? CompactSummaryJson { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
