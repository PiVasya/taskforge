using Xunit;

namespace TaskForge.Solutions.RatingWorker.Tests;

public sealed class AcceptedActivityProjectionTests
{
    [Fact]
    public void RepeatedAcceptedAttemptsAcrossServicesAwardRatingOnce()
    {
        var user = Guid.NewGuid();
        var assignment = Guid.NewGuid();
        var course = Guid.NewGuid();
        var time = new DateTimeOffset(2026, 10, 24, 10, 0, 0, TimeSpan.Zero);
        var rows = new[]
        {
            new Worker.RatingAcceptedRow("code", Guid.NewGuid(), user, assignment, course, 100, time),
            new Worker.RatingAcceptedRow("image", Guid.NewGuid(), user, assignment, course, 100, time.AddHours(1)),
            new Worker.RatingAcceptedRow("task", Guid.NewGuid(), user, Guid.NewGuid(), course, 25, time.AddMinutes(10))
        };
        var summary = Worker.SummarizeAccepted(rows);
        Assert.Equal(125, summary.TotalScore);
        Assert.Equal(2, summary.SolvedCount);
        Assert.Equal(time.AddHours(1), summary.LastAcceptedAt);
    }

    [Fact]
    public void MissingActivitiesYieldEmptyProjection()
    {
        var summary = Worker.SummarizeAccepted(Array.Empty<Worker.RatingAcceptedRow>());
        Assert.Equal(0, summary.TotalScore);
        Assert.Equal(0, summary.SolvedCount);
        Assert.Null(summary.LastAcceptedAt);
    }

    [Fact]
    public void FullRebuildMarkerMatchesLeaderboardReader()
    {
        Assert.Equal("RatingAcceptedActivitiesV1", Worker.ActivitiesReadyCheckpoint);
    }
}
