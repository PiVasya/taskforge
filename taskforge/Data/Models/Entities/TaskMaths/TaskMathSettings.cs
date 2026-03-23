using System.ComponentModel.DataAnnotations;

namespace taskforge.Data.Models.Entities
{
    /// <summary>
    /// Настройки математического задания (TaskAssignment.Type == "math").
    /// </summary>
    public sealed class TaskMathSettings
    {
        [Key]
        public Guid Id { get; set; }

        [Required]
        public Guid TaskAssignmentId { get; set; }

        public TaskAssignment TaskAssignment { get; set; } = null!;

        public int MaxAttempts { get; set; } = 1;
        public int PassPercent { get; set; } = 60;
        public bool ShuffleBlocks { get; set; } = false;
        public bool AllowReview { get; set; } = true;

        /// <summary>
        /// Таймер по попыткам, JSON-массив секунд: [120,90,null].
        /// </summary>
        public string? AttemptTimeLimitsJson { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }
}
