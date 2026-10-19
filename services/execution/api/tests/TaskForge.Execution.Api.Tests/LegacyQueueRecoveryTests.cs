using TaskForge.Execution.Api.Domain;
using TaskForge.Execution.Api.Services.Legacy;
using Xunit;

namespace TaskForge.Execution.Api.Tests;

public sealed class LegacyQueueRecoveryTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 18, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan RunningLimit = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan QueuedLimit = TimeSpan.FromMinutes(30);

    [Theory]
    [InlineData("running", 1, 3, 8, "Requeue")]
    [InlineData("running", 3, 3, 8, "Fail")]
    [InlineData("running", 2, 3, 1, "None")]
    [InlineData("queued", 0, 3, 31, "Fail")]
    [InlineData("queued", 3, 3, 1, "Fail")]
    [InlineData("queued", 1, 3, 1, "None")]
    public void ExpirationOnlyRequeuesLiveAttemptsWithinBudget(
        string status, int attempts, int maxAttempts, int ageMinutes, string expected)
    {
        var job = new ExecutionJob
        {
            Kind = ExecutionJobKinds.Legacy,
            Status = status,
            AttemptCount = attempts,
            StartedAt = Now.AddMinutes(-ageMinutes),
            CreatedAt = Now.AddMinutes(-ageMinutes)
        };
        Assert.Equal(expected, LegacyQueuePolicy.Expiration(job, Now, maxAttempts, RunningLimit, QueuedLimit).ToString());
    }

    [Fact]
    public void MissingLegacyStartTimeDoesNotLeaveRunningJobUnrecoverable()
    {
        var job = new ExecutionJob { Kind = ExecutionJobKinds.Legacy, Status = "running", AttemptCount = 3,
            StartedAt = null, CreatedAt = Now.AddMinutes(-45) };
        Assert.Equal(LegacyExpirationAction.Fail,
            LegacyQueuePolicy.Expiration(job, Now, 3, RunningLimit, QueuedLimit));
    }

    [Fact]
    public void CompletedAndSqlJobsAreNeverRecoveredAsLegacy()
    {
        var sqlJob = new ExecutionJob { Kind = "sql-check", Status = "queued", CreatedAt = Now.AddHours(-3) };
        var completed = new ExecutionJob { Kind = ExecutionJobKinds.Legacy, Status = "running", AttemptCount = 5,
            StartedAt = Now.AddMinutes(-20), CompletedAt = Now.AddMinutes(-10) };
        Assert.Equal(LegacyExpirationAction.None, LegacyQueuePolicy.Expiration(sqlJob, Now, 3, RunningLimit, QueuedLimit));
        Assert.Equal(LegacyExpirationAction.None, LegacyQueuePolicy.Expiration(completed, Now, 3, RunningLimit, QueuedLimit));
    }

    [Fact]
    public void ReplayedVerdictPreservesActualGradeAndCases()
    {
        const string raw = "{\"results\":[{\"passed\":true,\"hidden\":true,\"input\":\"HIDDEN\"}]}";
        var verdict = LegacyExecutionQueueMaintenance.SolutionResult("Accepted", 100, true, raw);
        Assert.Equal("Accepted", verdict.GetProperty("verdict").GetString());
        Assert.Equal(100, verdict.GetProperty("score").GetInt32());
        Assert.True(verdict.GetProperty("passedAllTests").GetBoolean());
        Assert.Equal(1, verdict.GetProperty("cases").GetArrayLength());
        Assert.True(verdict.GetProperty("cases")[0].GetProperty("hidden").GetBoolean());
    }

    [Fact]
    public void StoredWorkerVerdictIsReplayedWithoutLosingItsOriginalDetails()
    {
        const string original = "{\"verdict\":\"CompileError\",\"score\":0,\"passedAllTests\":false,\"compileError\":true,\"message\":\"syntax error\"}";
        var replay = LegacyExecutionQueueMaintenance.SolutionResult("CompileError", 0, false, original);
        Assert.Equal("syntax error", replay.GetProperty("message").GetString());
        Assert.True(replay.GetProperty("compileError").GetBoolean());
        Assert.Equal(original, replay.GetRawText());
    }

    [Fact]
    public void RecoverInfrastructureFailureWithoutAccidentallyAwardingScore()
    {
        var verdict = LegacyExecutionQueueMaintenance.SolutionResult("JudgeUnavailable", 0, false, "not valid json");
        Assert.Equal(0, verdict.GetProperty("score").GetInt32());
        Assert.False(verdict.GetProperty("passedAllTests").GetBoolean());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, verdict.GetProperty("raw").ValueKind);
    }
}
