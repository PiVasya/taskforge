using TaskForge.Tasks.Api.Services.Results;
using Xunit;

namespace TaskForge.Tasks.Api.Tests;

public sealed class PublicTaskStatisticsTests
{
    [Theory]
    [InlineData(true, true, false, "accepted")]
    [InlineData(false, true, false, "failed")]
    [InlineData(false, false, false, "pending")]
    [InlineData(false, false, true, "failed")]
    public void SubmittedAndUnsubmittedAttemptsHaveCorrectVerdicts(
        bool passed, bool submitted, bool expired, string expected)
        => Assert.Equal(expected, PublicTaskStatisticsService.Verdict(passed, submitted, expired));

    [Fact]
    public void OnlyAggregatesEscapeFromTheInternalTaskProjection()
    {
        var properties = typeof(PublicTaskStatistics).GetProperties().Select(x => x.Name).ToArray();
        Assert.Contains("DailyResults", properties);
        Assert.Contains("ResultTypes", properties);
        Assert.DoesNotContain("AnswersJson", properties);
        Assert.DoesNotContain("UserId", properties);
        Assert.DoesNotContain("ReviewJson", properties);
    }
}
