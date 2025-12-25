using System.ComponentModel.DataAnnotations;

namespace taskforge.Data.Models.Entities
{
    /// <summary>
    /// Попытка прохождения теста пользователем.
    /// </summary>
    public sealed class UserTaskTestAttempt
    {
        [Key]
        public Guid Id { get; set; }

        [Required]
        public Guid TaskAssignmentId { get; set; }

        public TaskAssignment TaskAssignment { get; set; } = null!;

        [Required]
        public Guid UserId { get; set; }

        public User User { get; set; } = null!;

        public int AttemptNumber { get; set; }

        public DateTime StartedAt { get; set; } = DateTime.UtcNow;
        public DateTime? SubmittedAt { get; set; }

        /// <summary>Ограничение по времени, в секундах. null = без таймера.</summary>
        public int? TimeLimitSeconds { get; set; }

        public int? ScorePercent { get; set; }
        public bool Passed { get; set; }
        public bool TimeExpired { get; set; }

        /// <summary>JSON-массив guid-ов вопросов в фактическом порядке показа.</summary>
        public string QuestionOrderJson { get; set; } = "[]";

        /// <summary>JSON с ответами пользователя (для просмотра/отладки).</summary>
        public string? AnswersJson { get; set; }
    }
}
