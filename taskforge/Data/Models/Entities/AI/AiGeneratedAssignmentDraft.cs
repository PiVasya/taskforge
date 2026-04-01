using System.ComponentModel.DataAnnotations;

namespace taskforge.Data.Models.Entities.AI;

public sealed class AiGeneratedAssignmentDraft
{
    [Key]
    public Guid Id { get; set; }

    [Required]
    public Guid JobId { get; set; }
    public AiJob Job { get; set; } = null!;

    public Guid? RequestedByUserId { get; set; }
    public User? RequestedByUser { get; set; }

    public Guid? ReviewedByUserId { get; set; }
    public User? ReviewedByUser { get; set; }

    public Guid? CourseId { get; set; }
    public Course? Course { get; set; }

    [Required, MaxLength(50)]
    public string AssignmentType { get; set; } = string.Empty;

    [Required, MaxLength(200)]
    public string Title { get; set; } = string.Empty;

    [Required]
    public string DraftJson { get; set; } = "{}";

    [Required, MaxLength(32)]
    public string Status { get; set; } = "draft";

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ReviewedAtUtc { get; set; }
}
