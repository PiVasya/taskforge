using System.ComponentModel.DataAnnotations;
namespace taskforge.Data.Models.Entities
{
    public class TaskAssignment
    {
        [Key] public Guid Id { get; set; }

        [Required, MaxLength(200)]
        public string Title { get; set; } = string.Empty;

        // Подробное описание с форматированием (markdown/html)
        [Required]
        public string Description { get; set; } = string.Empty;

        [Required]
        public Guid CourseId { get; set; }
        public Course Course { get; set; } = null!;

        // Тип задания (пока только "code-test", позже "quiz", "match", ...)
        [Required, MaxLength(50)]
        public string Type { get; set; } = "code-test";

        public string? Tags { get; set; } // можно хранить через запятую
        public int Difficulty { get; set; } = 1; // 1=легко, 2=средне, 3=сложно

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
        public int Sort { get; set; } = 0;

        // ===== image-test =====
        // Ключ эталонной картинки (храним именно ключ, а не публичный URL)
        public string? ImageTestReferenceKey { get; set; }

        // Порог совпадения в процентах (0..100). Если null — считается 90.
        public double? ImageTestSimilarityThreshold { get; set; }

        public ICollection<TaskTestCase> TestCases { get; set; } = new List<TaskTestCase>();
        public ICollection<UserTaskSolution> Solutions { get; set; } = new List<UserTaskSolution>();
    }
}