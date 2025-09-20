using System.ComponentModel.DataAnnotations;
using taskforge.Data.Models;

public class UserTaskSolution
{
    [Key] public Guid Id { get; set; }

    [Required]
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    [Required]
    public Guid TaskAssignmentId { get; set; }
    public TaskAssignment TaskAssignment { get; set; } = null!;

    [Required]
    public string SubmittedCode { get; set; } = string.Empty;

    [Required, MaxLength(50)]
    public string Language { get; set; } = "csharp";

    public bool PassedAllTests { get; set; }
    public int PassedCount { get; set; }
    public int FailedCount { get; set; }

    public DateTime SubmittedAt { get; set; } = DateTime.UtcNow;
}
