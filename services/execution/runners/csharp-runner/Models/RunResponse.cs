namespace Runner.Models;

public sealed class RunResponse
{
    public string Status { get; init; } = "ok";
    public string? Stdout { get; init; }
    public string? Stderr { get; init; }
    public int ExitCode { get; init; }
    public string? Error { get; init; }
}
