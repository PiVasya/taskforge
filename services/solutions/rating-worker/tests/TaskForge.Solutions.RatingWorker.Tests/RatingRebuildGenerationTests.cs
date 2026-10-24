using Xunit;

namespace TaskForge.Solutions.RatingWorker.Tests;

public sealed class RatingRebuildGenerationTests
{
    [Fact]
    public void NewlyRequestedRebuildRunsEvenIfLastSuccessfulRebuildWasRecent()
    {
        var now = new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
        Assert.True(Worker.ShouldRebuild(now.AddMinutes(-1), 4, 5, now, TimeSpan.FromHours(24)));
    }

    [Fact]
    public void ConcurrentNewRequestCannotBeSwallowedByEarlierCompletion()
    {
        var now = new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
        Assert.True(Worker.ShouldRebuild(now.AddSeconds(-1), 5, 6, now, TimeSpan.FromHours(24)));
        Assert.False(Worker.ShouldRebuild(now.AddSeconds(-1), 6, 6, now, TimeSpan.FromHours(24)));
    }

    [Fact]
    public void PeriodicSafetyNetStillRebuilds()
    {
        var now = new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
        Assert.True(Worker.ShouldRebuild(now.AddDays(-2), 3, 3, now, TimeSpan.FromHours(24)));
    }
}
