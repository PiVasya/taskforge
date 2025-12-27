using System.ComponentModel.DataAnnotations;

namespace taskforge.Data.Models.Entities
{
    /// <summary>
    /// Настройки тестового задания (TaskAssignment.Type == "test").
    /// </summary>
    public sealed class TaskTestSettings
    {
        [Key]
        public Guid Id { get; set; }

        [Required]
        public Guid TaskAssignmentId { get; set; }

        public TaskAssignment TaskAssignment { get; set; } = null!;

        /// <summary>Макс. количество попыток (0 или меньше = бесконечно).</summary>
        public int MaxAttempts { get; set; } = 1;

        /// <summary>Процент правильных ответов для зачёта (0..100).</summary>
        public int PassPercent { get; set; } = 60;

        /// <summary>Перемешивать вопросы.</summary>
        public bool ShuffleQuestions { get; set; } = true;

        /// <summary>Перемешивать варианты ответов (для single-choice).</summary>
        public bool ShuffleAnswers { get; set; } = true;

        /// <summary>
        /// Разрешить студенту просматривать свою попытку после отправки
        /// (видеть правильные/неправильные ответы).
        /// Если false — просмотр доступен только преподавателю/админу.
        /// </summary>
        public bool AllowReview { get; set; } = true;

        /// <summary>
        /// Таймер на попытку, в секундах, по номеру попытки.
        /// JSON-массив: [120, 90, null] и т.п.
        /// null/0/отрицательное = без таймера.
        /// </summary>
        public string? AttemptTimeLimitsJson { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }
}
