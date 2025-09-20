using System.ComponentModel.DataAnnotations;
using taskforge.Data.Models;

public class Course
{
    [Key] public Guid Id { get; set; }

    [Required, MaxLength(200)]
    public string Title { get; set; } = string.Empty;

    [MaxLength(4000)]
    public string? Description { get; set; }

    [Required]
    public Guid OwnerId { get; set; }
    public User Owner { get; set; } = null!;

    public bool IsPublic { get; set; } = false;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<TaskAssignment> Assignments { get; set; } = new List<TaskAssignment>();
}
