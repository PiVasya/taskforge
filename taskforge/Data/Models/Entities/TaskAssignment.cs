using System.ComponentModel.DataAnnotations;
using System.Text.Json;
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

        // CSV список разрешённых языков решения (например: "cpp,python,csharp")
        // Если null/пусто — ограничений нет (кроме image-test, где по умолчанию python/pascal).
        public string? AllowedLanguagesCsv { get; set; }

        public string? Tags { get; set; } // можно хранить через запятую
        public int Difficulty { get; set; } = 1; // 1=легко, 2=средне, 3=сложно

        // Рейтинг (вес) задания для топа. Если не задан — 1.
        // Используется при подсчёте мест в лидерборде.
        public int Rating { get; set; } = 1;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
        public int Sort { get; set; } = 0;

        // Признак, что задание было создано/опубликовано через AI-пайплайн.
        public bool IsAiGenerated { get; set; } = false;

        // ===== image-test =====
        // Ключ эталонной картинки (храним именно ключ, а не публичный URL)
        public string? ImageTestReferenceKey { get; set; }

        // Порог совпадения в процентах (0..100). Если null — считается 90.
        public double? ImageTestSimilarityThreshold { get; set; }

        // ===== Code policy (code-test / image-test) =====
        // Храним в БД как jsonb. Миграции пользователь делает сам.
        // Пример JSON: ["__import__", "Process.Start", "std::sort"]
        public JsonDocument? CodeForbiddenCallsJson { get; set; }

        // Пример JSON: ["solve", "Main"]
        public JsonDocument? CodeRequiredCallsJson { get; set; }

        public ICollection<TaskTestCase> TestCases { get; set; } = new List<TaskTestCase>();
        public ICollection<UserTaskSolution> Solutions { get; set; } = new List<UserTaskSolution>();
    }
}