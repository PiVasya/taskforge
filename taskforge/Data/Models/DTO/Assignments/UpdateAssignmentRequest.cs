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
        public string Description { get; set; } = string.Empty;

        [Required, MaxLength(30)]
        public string Type { get; set; } = "code-test";

        [Range(1, 3)]
        public int Difficulty { get; set; } = 1;

        public string? Tags { get; set; }

        // В UI мы делаем replace-all, поэтому Id можно игнорировать, но оставим для совместимости
        public IList<UpdateTestCaseDto> TestCases { get; set; } = new List<UpdateTestCaseDto>();
    }

    public sealed class UpdateTestCaseDto
    {
        public Guid? Id { get; set; } // не обязателен при replace-all

        [Required]
        public string Input { get; set; } = string.Empty;

        [Required]
        public string ExpectedOutput { get; set; } = string.Empty;

        public bool IsHidden { get; set; } = false;
    }
}
