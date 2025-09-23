namespace Runner.Models;
public sealed class RunRequest
{
    public required string Code { get; init; }
    public string? Input { get; init; }
}
