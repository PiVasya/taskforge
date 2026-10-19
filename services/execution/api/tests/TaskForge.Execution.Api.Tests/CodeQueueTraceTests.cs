using TaskForge.Execution.Api.Services.Legacy;
using Xunit;

namespace TaskForge.Execution.Api.Tests;

public sealed class CodeQueueTraceTests
{
    [Theory]
    [InlineData("node-a/container-123:ab12", "node-a/container-123:ab12")]
    [InlineData("", "unknown-pre-upgrade-worker")]
    [InlineData(null, "unknown-pre-upgrade-worker")]
    [InlineData("node-a\nINJECTED", "invalid-worker-label")]
    [InlineData("worker with spaces", "invalid-worker-label")]
    public void WorkerIdentityIsSafeForStructuredLogs(string? label, string expected)
        => Assert.Equal(expected, CodeQueueTrace.WorkerLabel(label));

    [Fact]
    public void LongOrControlCharacterLabelsAreRejected()
    {
        Assert.Equal("invalid-worker-label", CodeQueueTrace.WorkerLabel(new string('a', 121)));
        Assert.Equal("invalid-worker-label", CodeQueueTrace.WorkerLabel("a\r"));
    }

    [Fact]
    public void AgeNeverReturnsNegativeSeconds()
    {
        var now = DateTimeOffset.Parse("2026-10-19T19:00:00Z");
        Assert.Equal(600, CodeQueueTrace.AgeSeconds(now, now.AddMinutes(-10)));
        Assert.Equal(0, CodeQueueTrace.AgeSeconds(now, now.AddSeconds(1)));
    }
}
