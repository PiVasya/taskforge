namespace taskforge.Data.Models.DTO
{
    public class CompilerRunRequestDto
    {
        public string Language { get; set; } = string.Empty;
        public string Code { get; set; } = string.Empty;
        public string? Input { get; set; }
    }

    public class CompilerRunResponseDto
    {
        public string? Output { get; set; }
        public string? Error { get; set; }
    }

    public class TestCaseDto
    {
        public string? Input { get; set; }
        public string? ExpectedOutput { get; set; }
    }

    public class TestRunRequestDto
    {
        public string Language { get; set; } = string.Empty;
        public string Code { get; set; } = string.Empty;
        public List<TestCaseDto>? TestCases { get; set; }
    }

    public class TestResultDto
    {
        public string? Input { get; set; }
        public string? ExpectedOutput { get; set; }
        public string? ActualOutput { get; set; }
        public bool Passed { get; set; }
    }
}
