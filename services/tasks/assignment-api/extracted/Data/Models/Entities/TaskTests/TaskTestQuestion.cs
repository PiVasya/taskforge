using System.ComponentModel.DataAnnotations;

namespace taskforge.Data.Models.Entities
{
    /// <summary>
    /// Вопрос теста.
    /// Данные конкретного типа вопроса храним в DataJson (jsonb).
    /// </summary>
    public sealed class TaskTestQuestion
    {
        [Key]
        public Guid Id { get; set; }

        [Required]
        public Guid TaskAssignmentId { get; set; }

        public TaskAssignment TaskAssignment { get; set; } = null!;

        /// <summary>Порядок (если ShuffleQuestions = false).</summary>
        public int Order { get; set; }

        /// <summary>
        /// Тип: "single-choice" | "fill" | "text".
        /// </summary>
        [Required]
        [MaxLength(32)]
        public string Type { get; set; } = "single-choice";

        [Required]
        [MaxLength(4000)]
        public string Prompt { get; set; } = "";

        /// <summary>
        /// JSON (jsonb) с параметрами типа вопроса.
        /// Для "single-choice": { options:[{key,text}], correctOptionKeys:["..."] }
        /// Для "fill"/"text": { acceptedAnswers:["..."], caseSensitive:false, trim:true }
        /// </summary>
        [Required]
        public string DataJson { get; set; } = "{}";

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }
}
