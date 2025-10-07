namespace taskforge.Data.Models.DTO
{
    public class CompilerRunRequestDto
    {
        public string Language { get; set; } = string.Empty;
        public string Code { get; set; } = string.Empty;
        public string? Input { get; set; }

        // новые опциональные лимиты (мс и МБ)
        public int? TimeLimitMs { get; set; }    // например, 2000
        public int? MemoryLimitMb { get; set; }  // например, 256
    }

    public class CompilerRunResponseDto
    {
        // унифицированный ответ единичного запуска
        public string Status { get; set; } = "ok"; // ok | compile_error | runtime_error | time_limit | infrastructure_error
        public int ExitCode { get; set; }          // код процесса, при TL — 124
        public string? Stdout { get; set; }
        public string? Stderr { get; set; }

        // для compile_error удобно дублировать stderr компилятора
        public string? CompileStderr { get; set; }
        public string? Message { get; set; }       // человекочитаемое описание (опционально)
    }

    public class TestCaseDto
    {
        public string? Input { get; set; }
        public string? ExpectedOutput { get; set; }
        public bool IsHidden { get; set; }   // не обязательно, но пригодится на UI
    }

    public class TestRunRequestDto
    {
        public string Language { get; set; } = string.Empty;
        public string Code { get; set; } = string.Empty;
        public List<TestCaseDto>? TestCases { get; set; }

        // те же лимиты на прогон тестов
        public int? TimeLimitMs { get; set; }
        public int? MemoryLimitMb { get; set; }
    }

    public class TestResultDto
    {
        public string? Input { get; set; }
        public string? ExpectedOutput { get; set; }
        public string? ActualOutput { get; set; }
        public bool Passed { get; set; }

        // диагностическая часть по конкретному тесту
        public string Status { get; set; } = "ok"; // ok | compile_error | runtime_error | time_limit
        public int ExitCode { get; set; }
        public string? Stderr { get; set; }
        public string? CompileStderr { get; set; }
        public bool Hidden { get; set; }
    }
}
