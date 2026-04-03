using System.ComponentModel.DataAnnotations;

namespace taskforge.Data.Models.Entities.AI;

public sealed class AiReviewFinding
{
    [Key]
    public Guid Id { get; set; }

    public Guid? ArtifactId { get; set; }
    public AiArtifact? Artifact { get; set; }

    public Guid? DraftId { get; set; }
    public AiGeneratedAssignmentDraft? Draft { get; set; }

    [Required, MaxLength(80)]
    public string StageCode { get; set; } = string.Empty;

    [MaxLength(24)]
    public string? Severity { get; set; }

    [MaxLength(80)]
    public string? Code { get; set; }

    [Required]
    public string Message { get; set; } = string.Empty;

    public string? SuggestedRepair { get; set; }

    public double? Confidence { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
