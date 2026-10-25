using TaskForge.Solutions.Api.Services.Results;
using Xunit;

namespace TaskForge.Solutions.Api.Tests;

public sealed class UserPublicStatisticsTests
{
    [Theory]
    [InlineData("Accepted", "accepted")]
    [InlineData("accepted", "accepted")]
    [InlineData("Rejected", "failed")]
    [InlineData("CompileError", "failed")]
    [InlineData("TimeLimitExceeded", "failed")]
    [InlineData("Queued", "pending")]
    [InlineData("Running", "pending")]
    [InlineData("Preparing", "pending")]
    [InlineData("", "pending")]
    public void VerdictsAreGroupedWithoutTreatingPendingAsFailure(string status, string expected)
        => Assert.Equal(expected, UserPublicStatisticsService.VerdictCategory(status));

    [Theory]
    [InlineData("C++", "cpp")]
    [InlineData("C#", "csharp")]
    [InlineData("Py", "python")]
    [InlineData("PostgreSQL", "sql")]
    [InlineData("", "unknown")]
    public void LanguageKeysAreNormalized(string language, string expected)
        => Assert.Equal(expected, UserPublicStatisticsService.NormalizeLanguage(language));

    [Fact]
    public void DetailsDoNotContainSolutionBodiesOrPrivateAccountData()
    {
        var properties = typeof(UserPublicStatistics).GetProperties().Select(x => x.Name).ToArray();
        Assert.DoesNotContain("Code", properties);
        Assert.DoesNotContain("Email", properties);
        Assert.DoesNotContain("ResultJson", properties);
        Assert.Contains("DailyCode", properties);
        Assert.Contains("DailyRating", properties);
        Assert.Contains("DailyResults", properties);
        Assert.Contains("ResultTypes", properties);
        Assert.Contains("Rating", properties);
    }
}
