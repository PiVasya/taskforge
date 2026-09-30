using System.Text.Json;
using TaskForge.Tasks.Api.Services.Serialization;
using Xunit;

namespace TaskForge.Tasks.Api.Tests;

public sealed class AssignmentTypeNormalizationTests
{
    [Fact]
    public void ExplicitUnknownTypeIsRejectedInsteadOfFallingBackToCode()
    {
        Assert.False(AssignmentApiSerializationService.TryNormalizeAssignmentType("image-tset", out var normalized));
        Assert.Equal(string.Empty, normalized);
        Assert.Throws<InvalidOperationException>(() => AssignmentApiSerializationService.NormalizeExplicitAssignmentType("image-tset"));
    }

    [Theory]
    [InlineData("code", "code-test")]
    [InlineData("quiz", "test")]
    [InlineData("drawing", "image-test")]
    [InlineData("math-task", "math")]
    [InlineData("SQL-TEST", "sql-test")]
    public void ExplicitLegacyAliasIsCanonicalized(string source, string expected)
    {
        Assert.Equal(expected, AssignmentApiSerializationService.NormalizeExplicitAssignmentType(source));
    }

    [Fact]
    public void MissingTypeCanStillBeInferredFromLegacyShape()
    {
        using var document = JsonDocument.Parse("""{"questions":[]}""");
        Assert.Equal("test", AssignmentApiSerializationService.InferAssignmentTypeFromJson(document.RootElement, null));
    }

    [Fact]
    public void ExplicitUnknownTypeWinsOverLegacyShapeAndFailsClosed()
    {
        using var document = JsonDocument.Parse("""{"type":"image-tset","testCases":[]}""");
        Assert.Throws<InvalidOperationException>(() => AssignmentApiSerializationService.InferAssignmentTypeFromJson(document.RootElement, "image-tset"));
    }
}
