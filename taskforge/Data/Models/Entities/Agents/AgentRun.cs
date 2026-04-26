using System.ComponentModel.DataAnnotations;

namespace taskforge.Data.Models.Entities
{
    public sealed class AgentRun
    {
        [Key]
        public Guid Id { get; set; }

        [Required]
        public Guid ConversationId { get; set; }
        public AgentConversation Conversation { get; set; } = null!;

        [Required]
        public Guid RequestedByUserId { get; set; }
        public User RequestedByUser { get; set; } = null!;

        public Guid? ActingOnBehalfOfUserId { get; set; }
        public User? ActingOnBehalfOfUser { get; set; }

        /// <summary>queued / running / sleeping / waiting_approval / completed / failed / canceled</summary>
        [Required, MaxLength(32)]
        public string Status { get; set; } = "queued";

        [MaxLength(80)]
        public string? ScenarioId { get; set; }

        [MaxLength(128)]
        public string? WorkerId { get; set; }

        public int Priority { get; set; } = 0;
        public int Attempt { get; set; } = 0;

        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
        public DateTime? StartedAtUtc { get; set; }
        public DateTime? FinishedAtUtc { get; set; }
        public DateTime? LeaseExpiresAtUtc { get; set; }
        public DateTime? NextWakeAtUtc { get; set; }
        public DateTime? CanceledAtUtc { get; set; }

        /// <summary>Original request snapshot. Stored as jsonb.</summary>
        public string? RequestJson { get; set; }

        /// <summary>Final ResultEnvelope from worker. Stored as jsonb.</summary>
        public string? ResultJson { get; set; }

        public string? ErrorJson { get; set; }
        public string? DebugJson { get; set; }

        public ICollection<AgentStep> Steps { get; set; } = new List<AgentStep>();
        public ICollection<AgentRunArtifact> Artifacts { get; set; } = new List<AgentRunArtifact>();
    }
}
