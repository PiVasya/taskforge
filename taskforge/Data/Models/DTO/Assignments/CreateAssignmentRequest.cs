using System.ComponentModel.DataAnnotations;

namespace taskforge.Data.Models.DTO
{
    public sealed class CreateAssignmentRequest
    {
        [Required, MaxLength(200)]
        public string Title { get; set; } = string.Empty;

        [Required]
        public string Description { get; set; } = string.Empty; // markdown/html — как храните

        [Range(1, 3)]
        public int Difficulty { get; set; } = 1;

        public string? Tags { get; set; } // "strings,comma,separated"

        // Публичные + скрытые тесты (с флагом)
        [MinLength(1)]
        public IList<CreateTestCaseDto> TestCases { get; set; } = new List<CreateTestCaseDto>();
    }

    public sealed class CreateTestCaseDto
    {
        [Required]
        public string Input { get; set; } = string.Empty;

        [Required]
        public string ExpectedOutput { get; set; } = string.Empty;

        public bool IsHidden { get; set; } = false; // скрытый тест (не показываем юзеру)
    }
}
