using System.ComponentModel.DataAnnotations;

namespace taskforge.Data.Models.Entities
{
    public sealed class AgentMessage
    {
        [Key]
        public Guid Id { get; set; }

        [Required]
        public Guid ConversationId { get; set; }
        public AgentConversation Conversation { get; set; } = null!;

        public Guid? RunId { get; set; }
        public AgentRun? Run { get; set; }

        /// <summary>user / assistant / system / tool</summary>
        [Required, MaxLength(32)]
        public string Role { get; set; } = "assistant";

        [Required]
        public string Text { get; set; } = string.Empty;

        /// <summary>chat / worker / system / signalr</summary>
        [Required, MaxLength(64)]
        public string Source { get; set; } = "chat";

        /// <summary>Optional machine-readable payload. Stored as jsonb.</summary>
        public string? DataJson { get; set; }

        [MaxLength(128)]
        public string? ClientMessageId { get; set; }

        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    }
}
