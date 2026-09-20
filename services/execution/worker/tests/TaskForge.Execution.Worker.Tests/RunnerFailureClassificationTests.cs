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

}
