namespace Runner.Models;
public sealed class RunResponse
{
    public string? Stdout { get; init; }
    public string? Stderr { get; init; }
    public int ExitCode { get; init; }
    public string? Error { get; init; }
}
