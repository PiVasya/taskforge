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

        // ===== Draft / visibility =====
        // Hidden assignments are visible only to course editors/admins.
        // Used for AI-generated drafts before they are published into the main course flow.
        public bool IsHidden { get; set; } = false;

        // published | draft | polishing | ready | archived
        [MaxLength(32)]
        public string LifecycleStatus { get; set; } = "published";

        public bool IsAiDraft { get; set; } = false;

        public Guid? SourceAgentRunId { get; set; }
        public AgentRun? SourceAgentRun { get; set; }

        public Guid? SourceAgentArtifactId { get; set; }
        public AgentRunArtifact? SourceAgentArtifact { get; set; }

        public int? SourceAgentTaskIndex { get; set; }

        // Stored as jsonb. Contains original AI blueprint, polishing notes and runner validation.
        public string? AiDraftJson { get; set; }

        public DateTime? PolishedAtUtc { get; set; }
        public DateTime? PublishedAtUtc { get; set; }


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