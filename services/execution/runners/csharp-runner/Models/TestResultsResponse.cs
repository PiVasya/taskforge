namespace Runner.Models;
public sealed class TestResultsResponse
{
    public required IReadOnlyList<TestResult> Results { get; init; }
}
public sealed class TestResult
{
    public string? Input { get; init; }
    public string? ExpectedOutput { get; init; }
    public string ActualOutput { get; init; } = "";
    public bool Passed { get; init; }
    public string Status { get; init; } = "ok";
    public int ExitCode { get; init; } = 0;
    public string? Stderr { get; init; }
    public string? CompileStderr { get; init; }
    public bool Hidden { get; init; }
}
