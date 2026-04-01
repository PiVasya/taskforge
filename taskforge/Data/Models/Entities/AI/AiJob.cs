using System.ComponentModel.DataAnnotations;

namespace taskforge.Data.Models.Entities.AI;

public sealed class AiJob
{
    [Key]
    public Guid Id { get; set; }

    [Required, MaxLength(100)]
    public string Type { get; set; } = string.Empty;

    [Required, MaxLength(32)]
    public string Status { get; set; } = "pending";

    public int Priority { get; set; } = 0;

    public Guid? CreatedByUserId { get; set; }
    public User? CreatedByUser { get; set; }

    [MaxLength(160)]
    public string? CreatedByDisplayName { get; set; }

    [MaxLength(80)]
    public string? TargetEntityType { get; set; }

    public Guid? TargetEntityId { get; set; }

    public Guid? CourseId { get; set; }
    public Course? Course { get; set; }

    public string? InputJson { get; set; }
    public string? ResultJson { get; set; }
    public string? ErrorText { get; set; }

    [MaxLength(128)]
    public string? ModelName { get; set; }

    [MaxLength(128)]
    public string? WorkerId { get; set; }

    public int RetryCount { get; set; } = 0;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAtUtc { get; set; }
    public DateTime? HeartbeatAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public DateTime? NextAttemptAtUtc { get; set; }

    public ICollection<AiJobFile> Files { get; set; } = new List<AiJobFile>();
}
