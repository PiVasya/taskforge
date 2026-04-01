using System.ComponentModel.DataAnnotations;

namespace taskforge.Data.Models.Entities.AI;

public sealed class AiAssignmentInsight
{
    [Key]
    public Guid Id { get; set; }

    public Guid? JobId { get; set; }
    public AiJob? Job { get; set; }

    [Required]
    public Guid AssignmentId { get; set; }
    public TaskAssignment Assignment { get; set; } = null!;

    [Required, MaxLength(80)]
    public string Kind { get; set; } = "quality-audit";

    [Required]
    public string Summary { get; set; } = string.Empty;

    public string? SuggestionsJson { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
