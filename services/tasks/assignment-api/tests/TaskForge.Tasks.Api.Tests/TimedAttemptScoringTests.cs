using System.Text.Json;
using TaskForge.Tasks.Api.Contracts;
using TaskForge.Tasks.Api.Domain;
using TaskForge.Tasks.Api.Services.Common;
using TaskForge.Tasks.Api.Services.Math;
using TaskForge.Tasks.Api.Services.Testing;
using Xunit;

namespace TaskForge.Tasks.Api.Tests;

public sealed class TimedAttemptScoringTests
{
    [Fact]
    public void ExpiredAttempt_UsesEarnedScoreForPassDecision()
    {
        var outcome = AssignmentApiCommonService.ResolveTimedAttemptOutcome(75, 60, timeExpired: true);

        Assert.Equal(75, outcome.ScorePercent);
        Assert.True(outcome.Passed);
    }

    [Fact]
    public void ExpiredAttempt_BelowThreshold_RemainsFailedWithoutZeroingScore()
    {
        var outcome = AssignmentApiCommonService.ResolveTimedAttemptOutcome(40, 60, timeExpired: true);

        Assert.Equal(40, outcome.ScorePercent);
        Assert.False(outcome.Passed);
    }

    [Fact]
    public void ActiveAttempt_UsesEarnedScoreForPassDecision()
    {
        var outcome = AssignmentApiCommonService.ResolveTimedAttemptOutcome(75, 60, timeExpired: false);

        Assert.Equal(75, outcome.ScorePercent);
        Assert.True(outcome.Passed);
    }

    [Fact]
    public void ActiveAttempt_BelowThreshold_RemainsFailedWithoutZeroingScore()
    {
        var outcome = AssignmentApiCommonService.ResolveTimedAttemptOutcome(40, 60, timeExpired: false);

        Assert.Equal(40, outcome.ScorePercent);
        Assert.False(outcome.Passed);
    }


    [Fact]
    public void UnansweredTestQuestion_IsIncorrect()
    {
        var question = new TestQuestion(
            Guid.NewGuid(),
            0,
            "single-choice",
            "Pick B",
            [new Option("a", "A"), new Option("b", "B")],
            ["b"],
            [],
            false,
            true);

        Assert.False(AssignmentApiTestingService.IsTestCorrect(question, null));
    }

    [Fact]
    public void UnansweredMathBlock_IsIncorrect()
    {
        var block = new MathBlock(
            Guid.NewGuid(),
            0,
            "single-choice",
            "Pick B",
            null,
            1,
            true,
            [new Option("a", "A"), new Option("b", "B")],
            ["b"],
            [],
            false,
            true,
            null,
            [],
            [],
            [],
            []);

        Assert.False(AssignmentApiMathService.IsMathCorrect(block, null));
    }

    [Fact]
    public void TestStartDto_ExposesCanonicalAndLegacyTimeLimitFields()
    {
        var attempt = new TaskAttempt { TimeLimitSeconds = 90 };
        var spec = new TaskSpec(new TestSettings(1, false, 60, false, false, true, [90]), []);

        var dto = JsonSerializer.SerializeToElement(AssignmentApiTestingService.TestStartDto(attempt, spec));

        Assert.Equal(90, dto.GetProperty("timeLimitSeconds").GetInt32());
        Assert.Equal(90, dto.GetProperty("attemptTimeLimitSeconds").GetInt32());
    }

    [Fact]
    public void MathStartDto_ExposesCanonicalAndLegacyTimeLimitFields()
    {
        var attempt = new TaskAttempt { Kind = "math", TimeLimitSeconds = 120 };
        var spec = new MathSpec(new MathSettings(1, false, 60, false, true, [120]), []);

        var dto = JsonSerializer.SerializeToElement(AssignmentApiMathService.MathStartDto(attempt, spec));

        Assert.Equal(120, dto.GetProperty("timeLimitSeconds").GetInt32());
        Assert.Equal(120, dto.GetProperty("attemptTimeLimitSeconds").GetInt32());
    }
}
