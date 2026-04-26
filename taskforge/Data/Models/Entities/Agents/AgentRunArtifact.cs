using System.ComponentModel.DataAnnotations;

namespace taskforge.Data.Models.Entities
{
    public sealed class AgentRunArtifact
    {
        [Key]
        public Guid Id { get; set; }

        [Required]
        public Guid RunId { get; set; }
        public AgentRun Run { get; set; } = null!;

        [Required, MaxLength(80)]
        public string Type { get; set; } = "artifact";

        [Required, MaxLength(220)]
        public string Title { get; set; } = "AI artifact";

        /// <summary>Blueprint/report/draft payload. Stored as jsonb.</summary>
        public string DataJson { get; set; } = "{}";

        [MaxLength(512)]
        public string? StorageKey { get; set; }

        [MaxLength(128)]
        public string? ContentHash { get; set; }

        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    }
}
