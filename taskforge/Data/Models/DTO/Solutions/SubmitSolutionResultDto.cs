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
        public string Input { get; set; } = string.Empty;
        public string Expected { get; set; } = string.Empty;
        public string Actual { get; set; } = string.Empty;
        public bool Passed { get; set; }
        public bool Hidden { get; set; } // скрытый тест — на фронте можно «замаскировать» вход/ожидание
    }
}
