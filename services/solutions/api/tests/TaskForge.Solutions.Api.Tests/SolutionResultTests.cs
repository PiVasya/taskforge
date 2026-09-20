using System.Text.Json;
using TaskForge.Solutions.Api.Domain;
using TaskForge.Solutions.Api.Services.Common;
using TaskForge.Solutions.Api.Services.Mapping;
using TaskForge.Solutions.Api.Services.Results;
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
    [Fact]
    public void HiddenRunnerCases_AreRemovedFromOrdinarySolutionPayload_ButPreservedForAdmin()
    {
        using var source = JsonDocument.Parse("""
            {
              "cases": [
                { "input": "PUBLIC_INPUT", "expectedOutput": "PUBLIC_EXPECTED", "actualOutput": "PUBLIC_EXPECTED", "passed": true, "hidden": false },
                { "input": "SECRET_HIDDEN_INPUT", "expectedOutput": "SECRET_HIDDEN_EXPECTED", "actualOutput": "SECRET_HIDDEN_EXPECTED", "passed": true, "hidden": true }
              ],
              "raw": {
                "results": [
                  { "input": "PUBLIC_INPUT", "passed": true, "hidden": false },
                  { "input": "SECRET_RAW_HIDDEN", "passed": true, "isHidden": true }
                ]
              }
            }
            """);

        var ordinary = SolutionsApiResultsService.SanitizeSolutionResult(source.RootElement.Clone(), includeHiddenDetails: false);
        var admin = SolutionsApiResultsService.SanitizeSolutionResult(source.RootElement.Clone(), includeHiddenDetails: true);

        Assert.True(ordinary.HasValue);
        Assert.True(admin.HasValue);

        var ordinaryJson = ordinary.Value.GetRawText();
        var adminJson = admin.Value.GetRawText();

        Assert.Contains("PUBLIC_INPUT", ordinaryJson);
        Assert.DoesNotContain("SECRET_HIDDEN_INPUT", ordinaryJson);
        Assert.DoesNotContain("SECRET_HIDDEN_EXPECTED", ordinaryJson);
        Assert.DoesNotContain("SECRET_RAW_HIDDEN", ordinaryJson);

        Assert.Contains("SECRET_HIDDEN_INPUT", adminJson);
        Assert.Contains("SECRET_HIDDEN_EXPECTED", adminJson);
        Assert.Contains("SECRET_RAW_HIDDEN", adminJson);

        Assert.Equal((1, 0, 1), SolutionsApiMappingService.CountCases(ordinary));
        Assert.Equal((2, 0, 2), SolutionsApiMappingService.CountCases(admin));
    }

    [Theory]
    [InlineData(false, null, 50, false)]
    [InlineData(true, null, 50, true)]
    [InlineData(false, 7, 50, true)]
    [InlineData(false, 1000, 50, true)]
    [InlineData(false, null, 1000, true)]
    public void AdminHistory_AllMode_PreservesHistoricalPeriodSemantics(bool all, int? days, int take, bool expected)
    {
        Assert.Equal(expected, SolutionsApiMappingService.ShouldLoadAllAdminHistory(all, days, take));
    }

    [Fact]
    public void AdminHistorySummary_DoesNotEmbedCodeOrRunnerResult()
    {
        var userId = Guid.NewGuid();
        var row = new SolutionSubmission
        {
            Id = Guid.NewGuid(),
            AssignmentId = Guid.NewGuid(),
            UserId = userId,
            Language = "csharp",
            Code = "SECRET_SOURCE_CODE",
            Status = "Accepted",
            Score = 100,
            ResultJson = "{\"cases\":[{\"input\":\"SECRET_HIDDEN_INPUT\",\"hidden\":true}]}",
            CreatedAt = DateTimeOffset.UtcNow
        };

        var json = JsonSerializer.Serialize(SolutionsApiMappingService.ToAdminHistoryDto(row));

        Assert.Contains(row.Id.ToString(), json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(userId.ToString(), json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Accepted", json);
        Assert.DoesNotContain("SECRET_SOURCE_CODE", json);
        Assert.DoesNotContain("SECRET_HIDDEN_INPUT", json);
        Assert.DoesNotContain("result", json, StringComparison.OrdinalIgnoreCase);
    }

}
