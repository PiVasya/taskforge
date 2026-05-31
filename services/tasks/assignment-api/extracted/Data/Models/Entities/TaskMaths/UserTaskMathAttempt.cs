using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace taskforge.Data.Models.Entities
{
    [Table("UserTaskMathAttempts")]
    public sealed class UserTaskMathAttempt
    {
        [Key]
        public Guid Id { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        public Guid TaskAssignmentId { get; set; }
        public Guid UserId { get; set; }

        public TaskAssignment? TaskAssignment { get; set; }
        public User? User { get; set; }

        public int AttemptNumber { get; set; }
        public DateTime StartedAt { get; set; }
        public int? TimeLimitSeconds { get; set; }
        public bool TimeExpired { get; set; }
        public DateTime? SubmittedAt { get; set; }

        public int TotalScore { get; set; }
        public int EarnedScore { get; set; }
        public int ScorePercent { get; set; }
        public bool Passed { get; set; }

        public string? BlockOrderJson { get; set; }
        public string? AnswersJson { get; set; }
    }
}
