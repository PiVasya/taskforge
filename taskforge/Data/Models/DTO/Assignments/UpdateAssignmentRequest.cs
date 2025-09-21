using System.ComponentModel.DataAnnotations;

namespace taskforge.Data.Models.DTO
{
    /// <summary>
    /// Полное редактирование задания.
    /// Тест-кейсы передаются целиком и заменяют существующий список.
    /// </summary>
    public sealed class UpdateAssignmentRequest
    {
        [Required, MaxLength(200)]
        public string Title { get; set; } = string.Empty;

        [Required]
        public string Description { get; set; } = string.Empty; // markdown/html

        [Required, MaxLength(50)]
        public string Type { get; set; } = "code-test";

        public string? Tags { get; set; } // CSV
        public int Difficulty { get; set; } = 1; // 1..3

        /// <summary>
        /// Полная замена набора тестов.
        /// </summary>
        [Required]
        public IList<UpdateTestCaseDto> TestCases { get; set; } = new List<UpdateTestCaseDto>();
    }

    public sealed class UpdateTestCaseDto
    {
        public Guid? Id { get; set; } // можно передавать, но сейчас не используется (мы делаем replace-all)
        [Required] public string Input { get; set; } = string.Empty;
        [Required] public string ExpectedOutput { get; set; } = string.Empty;
        public bool IsHidden { get; set; } = false;
    }
}
