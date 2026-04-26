using System.ComponentModel.DataAnnotations;

namespace taskforge.Data.Models.Entities
{
    /// <summary>
    /// Persistent AI chat root. Migrations are intentionally not included; generate them from this entity.
    /// </summary>
    public sealed class AgentConversation
    {
        [Key]
        public Guid Id { get; set; }

        [Required]
        public Guid UserId { get; set; }
        public User User { get; set; } = null!;

        public Guid? CourseId { get; set; }
        public Course? Course { get; set; }

        public Guid? AssignmentId { get; set; }
        public TaskAssignment? Assignment { get; set; }

        public Guid? SupportTicketId { get; set; }
        public SupportTicket? SupportTicket { get; set; }

        [Required, MaxLength(220)]
        public string Title { get; set; } = "AI-чат";

        [Required, MaxLength(64)]
        public string Mode { get; set; } = "course-assistant";

        /// <summary>
        /// Compact rolling memory for the external worker. Stored as jsonb.
        /// </summary>
        public string? MemoryJson { get; set; }

        public bool IsArchived { get; set; } = false;

        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

        public ICollection<AgentMessage> Messages { get; set; } = new List<AgentMessage>();
        public ICollection<AgentRun> Runs { get; set; } = new List<AgentRun>();
    }
}
