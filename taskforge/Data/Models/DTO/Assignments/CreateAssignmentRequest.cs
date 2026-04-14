using System.ComponentModel.DataAnnotations;

namespace taskforge.Data.Models.DTO
{
    public sealed class CreateAssignmentRequest
    {
        [Required, MaxLength(200)]
        public string Title { get; set; } = string.Empty;

        [Required]
        public string Description { get; set; } = string.Empty; // markdown/html

        // Поддерживаемые типы:
        // "code-test" — проверка тестами (классическая задача)
        // "test" — тест/опрос
        // "image-test" — проверка по картинке (подключим позже)
        [Required, MaxLength(30)]
        public string Type { get; set; } = "code-test";

        // Разрешённые языки решения для code-test / image-test.
        // Пример: ["cpp","python"]
        public IList<string>? AllowedLanguages { get; set; }

        [Range(1, 3)]
        public int Difficulty { get; set; } = 1;

        // Рейтинг (вес) задания для топа. Если не указан — будет 1.
        // Любое целое число >= 0.
        [Range(0, int.MaxValue)]
        public int? Rating { get; set; }

        public string? Tags { get; set; } // "строки,через,запятую"
        public int? Sort { get; set; }

        // Публичные и скрытые тесты
        // Валидация зависит от Type:
        // - "code-test": требуется хотя бы 1 тест
        // - "test" / "image-test": может быть пусто
        // --- Code policy: required/forbidden calls (per assignment) ---
        // Each entry is a function/method name that must be CALLED at least once.
        // Examples: ["solve"], ["Process.Start"], ["std::sort"].
        public IList<string>? CodeRequiredCalls { get; set; }

        // Each entry is a function/method name that must NOT be called.
        public IList<string>? CodeForbiddenCalls { get; set; }

        public IList<CreateTestCaseDto> TestCases { get; set; } = new List<CreateTestCaseDto>();
    }

    public sealed class CreateTestCaseDto
    {
        public string? Input { get; set; }

        public string? ExpectedOutput { get; set; }

        public bool IsHidden { get; set; } = false;
    }

}