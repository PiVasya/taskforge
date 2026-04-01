using System.ComponentModel.DataAnnotations;

namespace taskforge.Data.Models.Entities.AI;

public sealed class AiSubmissionReview
{
    [Key]
    public Guid Id { get; set; }

    public Guid? JobId { get; set; }
    public AiJob? Job { get; set; }

    public Guid? UserId { get; set; }
    public User? User { get; set; }

    public Guid? AssignmentId { get; set; }
    public TaskAssignment? Assignment { get; set; }

    [MaxLength(80)]
    public string? SourceType { get; set; }

    public Guid? SourceAttemptId { get; set; }

    [Required, MaxLength(40)]
    public string Verdict { get; set; } = "needs-review";

    public double? Score { get; set; }

    [Required]
    public string Summary { get; set; } = string.Empty;

    public string? SignalsJson { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
