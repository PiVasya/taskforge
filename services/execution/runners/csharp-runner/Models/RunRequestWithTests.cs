namespace Runner.Models;

public sealed class RunRequestWithTests : RunRequest
{
    public List<TestCase>? Tests { get; init; }
}
