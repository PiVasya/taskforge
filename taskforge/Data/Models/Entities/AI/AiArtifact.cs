using System.ComponentModel.DataAnnotations;

namespace taskforge.Data.Models.Entities.AI;

public sealed class AiArtifact
{
    [Key]
    public Guid Id { get; set; }

    public Guid JobId { get; set; }
    public AiJob? Job { get; set; }

    public Guid? DraftId { get; set; }
    public AiGeneratedAssignmentDraft? Draft { get; set; }

    [Required, MaxLength(80)]
    public string ArtifactType { get; set; } = string.Empty;

    [MaxLength(80)]
    public string? StageCode { get; set; }

    [MaxLength(32)]
    public string? Status { get; set; }

    public string? PayloadJson { get; set; }

    [MaxLength(128)]
    public string? ModelName { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public ICollection<AiReviewFinding> Findings { get; set; } = new List<AiReviewFinding>();
}
