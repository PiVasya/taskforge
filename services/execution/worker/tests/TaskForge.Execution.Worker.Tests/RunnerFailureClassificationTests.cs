using System.Net;
using System.Text.Json;
using Xunit;

namespace TaskForge.Execution.Worker.Tests;

public sealed class RunnerFailureClassificationTests
{
    [Theory]
    [InlineData("fork: Too many open files (EMFILE)")]
    [InlineData("ENFILE")]
    public void InfrastructureDiagnostics_AreRecognized(string diagnostic)
        => Assert.True(Worker.IsRunnerInfrastructureDiagnostic(diagnostic));

    [Fact]
    public void RunnerCaseArray_DoesNotNeedRedundantRawCopy()
    {
        using var raw = JsonDocument.Parse("{\"results\":[{\"passed\":false,\"actualOutput\":\"Hello\"}]}");
        var cases = raw.RootElement.GetProperty("results");
        Assert.True(Worker.RawDuplicatesResults(raw.RootElement, cases));
    }

    [Fact]
    public void RunnerRawMetadata_RemainsAvailable()
    {
        using var raw = JsonDocument.Parse("{\"results\":[],\"policyKind\":\"task\"}");
        var cases = raw.RootElement.GetProperty("results");
        Assert.False(Worker.RawDuplicatesResults(raw.RootElement, cases));
    }

    [Fact]
    public void OutputLimitCase_IsDistinguishedFromOrdinaryWrongAnswer()
    {
        using var outputLimit = JsonDocument.Parse("{\"status\":\"output_limit\",\"passed\":false}");
        using var mismatch = JsonDocument.Parse("{\"status\":\"ok\",\"passed\":false}");
        Assert.True(Worker.IsOutputLimitResult(outputLimit.RootElement));
        Assert.False(Worker.IsOutputLimitResult(mismatch.RootElement));
    }

    [Theory]
    [InlineData("time_limit")]
    [InlineData("time_limit_exceeded")]
    [InlineData("TimeLimitExceeded")]
    public void RunnerTimeLimitCase_IsClassifiedAsTimeout(string status)
    {
        using var doc = JsonDocument.Parse($"{{\"status\":\"{status}\",\"passed\":false,\"exitCode\":124}}");
        Assert.True(Worker.IsTimeLimitResult(doc.RootElement));
        Assert.True(Worker.IsTimeLimitRoot(doc.RootElement));
        Assert.False(Worker.IsOutputLimitResult(doc.RootElement));
    }

    [Fact]
    public void OrdinaryWrongAnswer_DoesNotBecomeTimeLimitExceeded()
    {
        using var doc = JsonDocument.Parse("{\"status\":\"ok\",\"passed\":false}");
        Assert.False(Worker.IsTimeLimitResult(doc.RootElement));
    }

    [Fact]
    public async Task RunnerResponseIsCappedEvenWithMissingContentLength()
    {
        var data = new byte[Worker.MaxRunnerResponseBytes + 1];
        var content = new StreamContent(new MemoryStream(data));
        await Assert.ThrowsAsync<InvalidDataException>(
            () => Worker.ReadBoundedRunnerResponseAsync(content, CancellationToken.None));
    }

    [Fact]
    public async Task SmallRunnerResponseRemainsReadable()
    {
        var content = new StringContent("{\"results\":[]}");
        var text = await Worker.ReadBoundedRunnerResponseAsync(content, CancellationToken.None);
        Assert.Equal("{\"results\":[]}", text);
    }

    [Fact]
    public void StudentMismatch_IsNotInfrastructureFailure()
        => Assert.False(Worker.IsRunnerInfrastructureDiagnostic("student output mismatch"));

    [Theory]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public void GatewayFailures_AreTransient(HttpStatusCode status)
        => Assert.True(Worker.IsTransientRunnerStatus(status));

    [Fact]
    public void BadRequest_IsNotTransient()
        => Assert.False(Worker.IsTransientRunnerStatus(HttpStatusCode.BadRequest));

    [Fact]
    public void JudgeUnavailable_RecognizesStatusAndNestedInfrastructureDiagnostic()
    {
        using var unavailable = JsonDocument.Parse("{\"status\":\"judge_unavailable\"}");
        using var nested = JsonDocument.Parse("{\"error\":\"Too many open files\"}");
        using var ordinary = JsonDocument.Parse("{\"status\":\"wrong_answer\"}");

        Assert.True(Worker.IsJudgeUnavailableRoot(unavailable.RootElement));
        Assert.True(Worker.IsJudgeUnavailableRoot(nested.RootElement));
        Assert.False(Worker.IsJudgeUnavailableRoot(ordinary.RootElement));
    }

    [Theory]
    [InlineData("/tmp/taskforge-abc/source.cs", "/tmp/taskforge-abc")]
    [InlineData("/tmp/go-build1234/b001/exe/main", "/tmp/go-build1234")]
    [InlineData("/app/internal/runner", "/app/internal")]
    public void InternalRunnerPaths_DoNotLeakServerFilesystem(string diagnostic, string privatePrefix)
    {
        var sanitized = Worker.SanitizeRunnerText(diagnostic);

        Assert.DoesNotContain(privatePrefix, sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("/tmp/", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("/app/", sanitized, StringComparison.Ordinal);
        Assert.Contains("[", sanitized, StringComparison.Ordinal);
    }

    [Fact]
    public void PolicyFailureMessage_PreservesSafeAssignmentReason()
    {
        using var policy = JsonDocument.Parse("""
        {
          "errors": [
            {"code":"forbidden_call","pattern_id":"task.rule","message":"Запрещён вызов foo"}
          ]
        }
        """);

        var message = Worker.BuildPolicyMessage(policy.RootElement);

        Assert.Contains("Код не соответствует правилам задания:", message, StringComparison.Ordinal);
        Assert.Contains("Запрещён вызов foo", message, StringComparison.Ordinal);
    }
    [Fact]
    public void PolicyFailurePayload_ExposesSafeTaskRuleLocation()
    {
        const string source = "int main() {\n  forbiddenCall();\n}";
        using var policy = JsonDocument.Parse("""
        {
          "errors": [
            {"code":"forbidden_call","pattern_id":"task.forbidden_call","message":"Запрещено: forbiddenCall"}
          ],
          "hits": [
            {"pattern_id":"task.forbidden_call","needle":"forbiddenCall","position":15,"preview":"forbiddenCall();"}
          ]
        }
        """);

        var payload = Worker.BuildClientPolicyPayload(policy.RootElement, source);
        var error = payload.GetProperty("errors").EnumerateArray().First();

        Assert.Equal("forbiddenCall", error.GetProperty("needle").GetString());
        Assert.Equal(2, error.GetProperty("line").GetInt32());
        Assert.True(error.GetProperty("column").GetInt32() > 0);
        Assert.Contains("forbiddenCall", error.GetProperty("preview").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void PolicyFailurePayload_DoesNotExposeSensitivePlatformLocation()
    {
        const string source = "System.IO.File.Delete(\"x\");";
        using var policy = JsonDocument.Parse("""
        {
          "errors": [
            {"code":"security_policy","pattern_id":"cs.files","message":"blocked"}
          ],
          "hits": [
            {"pattern_id":"cs.files","needle":"System.IO.File","position":0,"preview":"System.IO.File.Delete"}
          ]
        }
        """);

        var payload = Worker.BuildClientPolicyPayload(policy.RootElement, source);
        var error = payload.GetProperty("errors").EnumerateArray().First();

        Assert.Equal("platform.security", error.GetProperty("pattern_id").GetString());
        Assert.False(error.TryGetProperty("position", out _));
        Assert.False(error.TryGetProperty("preview", out _));
        Assert.Equal(0, payload.GetProperty("hits").GetArrayLength());
    }


    [Theory]
    [InlineData("{\"results\":[{\"status\":\"ok\",\"passed\":false}]}", true)]
    [InlineData("{\"results\":[{\"status\":\"ok\",\"passed\":true}]}", true)]
    [InlineData("{\"results\":[{\"status\":\"time_limit\",\"passed\":false}]}", false)]
    [InlineData("{\"results\":[{\"status\":\"compile_error\",\"passed\":false}]}", false)]
    [InlineData("{\"results\":[{\"status\":\"output_limit\",\"passed\":false}]}", false)]
    [InlineData("{\"results\":[{\"status\":\"policy_error\",\"passed\":false}]}", false)]
    public void IncompleteBatch_OnlyTerminalFailuresAreAllowedToEndEarly(string json, bool incomplete)
    {
        using var doc = JsonDocument.Parse(json);
        var results = doc.RootElement.GetProperty("results");
        Assert.Equal(incomplete, Worker.IsIncompleteRunnerBatch(doc.RootElement, results, 5));
    }

    [Theory]
    [InlineData(0, 5, 0)]
    [InlineData(1, 5, 20)]
    [InlineData(2, 5, 40)]
    [InlineData(5, 5, 100)]
    public void ShortCircuitVerdicts_UseTotalRequestedTestsForScore(int passed, int requested, int expected)
        => Assert.Equal(expected, Worker.ComputeRunnerScore(passed, requested));

    [Theory]
    [InlineData("{}", true)]
    [InlineData("{\"status\":\"time_limit\"}", false)]
    public void MissingRunnerResults_AreNotSilentlyGradedRejected(string json, bool incomplete)
    {
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(incomplete, Worker.IsIncompleteRunnerBatch(doc.RootElement, null, 5));
    }

    [Fact]
    public void CompilationTimeoutWithDiagnostic_MustNotBecomeCompileError()
    {
        using var test = JsonDocument.Parse("{\"status\":\"time_limit\",\"compileStderr\":\"Compilation time limit exceeded\"}");
        Assert.True(Worker.IsTimeLimitResult(test.RootElement));
        Assert.False(Worker.IsCompileErrorResult(test.RootElement));
        Assert.False(Worker.IsCompileErrorRoot(test.RootElement));
    }

    [Fact]
    public async Task InternalFailureResponsePreviewIsBounded()
    {
        using var content = new StreamContent(new MemoryStream(new byte[Worker.MaxInternalErrorResponseBytes + 1024]));
        var preview = await Worker.ReadInternalErrorPreviewAsync(content, CancellationToken.None);
        Assert.Equal(Worker.MaxInternalErrorResponseBytes, System.Text.Encoding.UTF8.GetByteCount(preview));
    }

}
