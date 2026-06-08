namespace Runner.Models;
public sealed class TestCase
{
    public string? Input { get; init; }
    public string? ExpectedOutput { get; init; }
    public bool IsHidden { get; init; }
}
