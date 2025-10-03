using System.ComponentModel.DataAnnotations;

namespace taskforge.Data.Models.DTO
{
    public sealed class CreateAssignmentRequest
    {
        [Required, MaxLength(200)]
        public string Title { get; set; } = string.Empty;

        [Required]
        public string Description { get; set; } = string.Empty; // markdown/html

        // "code-test" — текущий поддерживаемый тип
        [Required, MaxLength(30)]
        public string Type { get; set; } = "code-test";

        [Range(1, 3)]
        public int Difficulty { get; set; } = 1;

        public string? Tags { get; set; } // "строки,через,запятую"
        public int? Sort { get; set; }

        // Публичные и скрытые тесты
        [MinLength(1)]
        public IList<CreateTestCaseDto> TestCases { get; set; } = new List<CreateTestCaseDto>();
    }

    public sealed class CreateTestCaseDto
    {
        [Required]
        public string Input { get; set; } = string.Empty;

        [Required]
        public string ExpectedOutput { get; set; } = string.Empty;

        public bool IsHidden { get; set; } = false;
    }

}
