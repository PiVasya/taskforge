namespace taskforge.Data.Models.DTO
{
    public sealed class SubmitSolutionResultDto
    {
        public bool PassedAll { get; set; }
        public int Passed { get; set; }
        public int Failed { get; set; }

        public IList<SolutionCaseResultDto> Cases { get; set; } = new List<SolutionCaseResultDto>();
    }

    public sealed class SolutionCaseResultDto
    {
        // For hidden tests we may mask these fields for students.
        public string? Input { get; set; }
        public string? Expected { get; set; }
        public string? Actual { get; set; }
        public bool Passed { get; set; }
        public bool Hidden { get; set; } // скрытый тест — на фронте можно «замаскировать» вход/ожидание

        // Diagnostics (needed by UI to show *why* the solution failed, incl. policy_failed).
        public string? Status { get; set; }         // ok | compile_error | runtime_error | time_limit | policy_failed
        public int? ExitCode { get; set; }
        public string? Stderr { get; set; }
        public string? CompileStderr { get; set; }
    }
}
