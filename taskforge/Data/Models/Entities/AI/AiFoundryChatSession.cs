using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace taskforge.Data.Models.Entities.AI;

public sealed class AiFoundryChatSession
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid? CourseId { get; set; }

    public Guid? CreatedByUserId { get; set; }

    [MaxLength(200)]
    public string Title { get; set; } = string.Empty;

    [Column(TypeName = "jsonb")]
    public string MessagesJson { get; set; } = "[]";

    [Column(TypeName = "jsonb")]
    public string? PlanJson { get; set; }

    [Column(TypeName = "timestamp with time zone")]
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    [Column(TypeName = "timestamp with time zone")]
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    public Course? Course { get; set; }
    public User? CreatedByUser { get; set; }
}
