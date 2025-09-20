using System.ComponentModel.DataAnnotations;

namespace taskforge.Data.Models.DTO
{
    public sealed class CreateAssignmentRequest
    {
        [Required, MaxLength(200)] public string Title { get; set; } = string.Empty;
        [Required] public string Description { get; set; } = string.Empty; // markdown/html
        [MaxLength(50)] public string Type { get; set; } = "code-test";
        public string? Tags { get; set; }
        public int Difficulty { get; set; } = 1;

        // Тесты сразу при создании (можно пусто — добавишь потом отдельным API)
        public List<CreateTestCaseRequest> TestCases { get; set; } = new();
    }

    public sealed class CreateTestCaseRequest
    {
        [Required] public string Input { get; set; } = string.Empty;
        [Required] public string ExpectedOutput { get; set; } = string.Empty;
        public bool IsHidden { get; set; } = false;
    }

    public sealed class AssignmentListItemDto
    {
        public Guid Id { get; set; }
        public string Title { get; set; } = string.Empty;
        public string? Description { get; set; }
        public int Difficulty { get; set; }
        public string? Tags { get; set; }
        public DateTime CreatedAt { get; set; }
        public bool SolvedByCurrentUser { get; set; }
    }

    public sealed class AssignmentDetailsDto
    {
        public Guid Id { get; set; }
        public Guid CourseId { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty; // markdown/html
        public string Type { get; set; } = "code-test";
        public string? Tags { get; set; }
        public int Difficulty { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }

        // Показываем только публичные примеры (не скрытые)
        public List<PublicSampleDto> PublicSamples { get; set; } = new();
        public int TotalTests { get; set; }
        public int PublicTests { get; set; }
        public bool SolvedByCurrentUser { get; set; }
    }

    public sealed class PublicSampleDto
    {
        public string Input { get; set; } = string.Empty;
        // ExpectedOutput — по желанию: обычно не показывают, оставлю пустым
    }
}
