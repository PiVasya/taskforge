using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace taskforge.Data.Models.Entities
{
    [Table("UserTaskTestAttempts")]
    public sealed class UserTaskTestAttempt
    {
        [Key]
        public Guid Id { get; set; }

        // В БД CreatedAt/UpdatedAt NOT NULL — поэтому держим всегда заполненными.
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        public Guid TaskAssignmentId { get; set; }
        public Guid UserId { get; set; }

        // ✅ Эти навигации нужны, потому что DbContext их использует в HasOne(...)
        public TaskAssignment? TaskAssignment { get; set; }
        public User? User { get; set; }

        public int AttemptNumber { get; set; }

        public DateTime StartedAt { get; set; }

        public int? TimeLimitSeconds { get; set; }

        public bool TimeExpired { get; set; }

        public DateTime? SubmittedAt { get; set; }

        public int ScorePercent { get; set; }

        public bool Passed { get; set; }

        public string? QuestionOrderJson { get; set; }

        public string? AnswersJson { get; set; }
    }
}
