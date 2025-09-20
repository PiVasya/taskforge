using System.ComponentModel.DataAnnotations;

namespace taskforge.Data.Models.DTO
{
    public sealed class SubmitSolutionRequest
    {
        [Required, MaxLength(50)] public string Language { get; set; } = "csharp";
        [Required] public string Code { get; set; } = string.Empty;
    }

    public sealed class SubmitSolutionResponse
    {
        public bool PassedAllTests { get; set; }
        public int PassedCount { get; set; }
        public int FailedCount { get; set; }
        public List<TestRunResultDto> Results { get; set; } = new();
    }

    public sealed class TestRunResultDto
    {
        public string Input { get; set; } = string.Empty;
        public string? ActualOutput { get; set; }
        public bool Passed { get; set; }
        public bool IsHidden { get; set; }
        // ExpectedOutput намеренно не возвращаем (особенно для hidden)
    }
}
