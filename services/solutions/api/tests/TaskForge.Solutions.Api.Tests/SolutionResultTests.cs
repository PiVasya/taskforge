using System.Text.Json;
using TaskForge.Solutions.Api.Services.Common;
using Xunit;

namespace TaskForge.Solutions.Api.Tests;

public sealed class SolutionResultTests
{
    [Theory]
    [InlineData("ok")]
    [InlineData("accepted")]
    [InlineData("passed")]
    [InlineData("success")]
    public void PassedStatusVariants_AreAccepted(string status)
    {
        using var result = JsonDocument.Parse($"{{\"status\":\"{status}\"}}");

        Assert.True(SolutionsApiCommonService.IsPassedResult(result.RootElement));
    }

    [Theory]
    [InlineData("wrong_answer")]
    [InlineData("runtime_error")]
    [InlineData("compile_error")]
    public void FailureStatusVariants_AreRejected(string status)
    {
        using var result = JsonDocument.Parse($"{{\"status\":\"{status}\"}}");

        Assert.False(SolutionsApiCommonService.IsPassedResult(result.RootElement));
    }

    [Fact]
    public void ExplicitPassedFlag_IsAuthoritativeOverStatusFallback()
    {
        using var failed = JsonDocument.Parse("{\"passed\":false,\"status\":\"ok\"}");
        using var passed = JsonDocument.Parse("{\"passed\":true,\"status\":\"wrong_answer\"}");

        Assert.False(SolutionsApiCommonService.IsPassedResult(failed.RootElement));
        Assert.True(SolutionsApiCommonService.IsPassedResult(passed.RootElement));
    }

    [Fact]
    public void CompileError_IsRecognizedFromStatusOrCompilerDiagnostic()
    {
        using var status = JsonDocument.Parse("{\"status\":\"compile_error\"}");
        using var stderr = JsonDocument.Parse("{\"compileStderr\":\"syntax error\"}");
        using var ordinary = JsonDocument.Parse("{\"status\":\"wrong_answer\"}");

        Assert.True(SolutionsApiCommonService.IsCompileErrorResult(status.RootElement));
        Assert.True(SolutionsApiCommonService.IsCompileErrorResult(stderr.RootElement));
        Assert.False(SolutionsApiCommonService.IsCompileErrorResult(ordinary.RootElement));
    }
}
