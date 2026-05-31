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
}
