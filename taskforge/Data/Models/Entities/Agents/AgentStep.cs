using System.ComponentModel.DataAnnotations;

namespace taskforge.Data.Models.Entities
{
    public sealed class AgentStep
    {
        [Key]
        public Guid Id { get; set; }

        [Required]
        public Guid RunId { get; set; }
        public AgentRun Run { get; set; } = null!;

        public int Seq { get; set; }

        [Required, MaxLength(64)]
        public string Kind { get; set; } = "worker";

        [Required, MaxLength(32)]
        public string Status { get; set; } = "completed";

        [MaxLength(128)]
        public string? ActionName { get; set; }

        [MaxLength(220)]
        public string? Title { get; set; }

        [MaxLength(2000)]
        public string? Summary { get; set; }

        public string? InputJson { get; set; }
        public string? OutputJson { get; set; }
        public string? ErrorJson { get; set; }

        public bool IsVisibleToUser { get; set; } = true;

        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
        public DateTime? StartedAtUtc { get; set; }
        public DateTime? FinishedAtUtc { get; set; }
    }
}
