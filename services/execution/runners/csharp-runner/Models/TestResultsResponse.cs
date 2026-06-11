namespace Runner.Models;
public sealed class TestResultsResponse
{
    public required IReadOnlyList<TestResult> Results { get; set; }
}
public sealed class TestResult
{
    public string? Input { get; set; }
    public string? ExpectedOutput { get; set; }
    public string ActualOutput { get; set; } = "";
    public bool Passed { get; set; }
    public string Status { get; set; } = "ok";
    public int ExitCode { get; set; } = 0;
    public string? Stderr { get; set; }
    public string? CompileStderr { get; set; }
    public bool Hidden { get; set; }
}
