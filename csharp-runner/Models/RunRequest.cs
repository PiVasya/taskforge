namespace Runner.Models;
public class RunRequest
{
    public required string Code { get; init; }
    public string? Input { get; init; }
}
