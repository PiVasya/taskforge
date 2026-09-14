using System.Text.Json;
using System.Text.Json.Nodes;
using TaskForge.Ai.Api.Services.Mapping;
using Xunit;

namespace TaskForge.Ai.Api.Tests;

public sealed class AssignmentPayloadTests
{
    [Fact]
    public void ReferenceSolution_NeverBecomesLearnerStarterCode()
    {
        using var payload = JsonDocument.Parse("{}");
        var data = new JsonObject
        {
            ["title"] = "Generated task",
            ["referenceSolution"] = "SECRET_REFERENCE_ANSWER",
            ["language"] = "cpp"
        };

        var serialized = JsonSerializer.SerializeToElement(AiApiMappingService.BuildAssignmentPayload(data, payload.RootElement));

        Assert.Equal(string.Empty, serialized.GetProperty("starterCode").GetString());
        Assert.DoesNotContain("SECRET_REFERENCE_ANSWER", serialized.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public void ExplicitStarterCode_IsPreservedWithoutReferenceLeakage()
    {
        using var payload = JsonDocument.Parse("{}");
        var data = new JsonObject
        {
            ["title"] = "Generated task",
            ["starterCode"] = "Console.WriteLine(42);",
            ["referenceSolution"] = "SECRET_REFERENCE_ANSWER"
        };

        var serialized = JsonSerializer.SerializeToElement(AiApiMappingService.BuildAssignmentPayload(data, payload.RootElement));

        Assert.Equal("Console.WriteLine(42);", serialized.GetProperty("starterCode").GetString());
        Assert.DoesNotContain("SECRET_REFERENCE_ANSWER", serialized.GetRawText(), StringComparison.Ordinal);
    }
}
