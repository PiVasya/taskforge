using System.Text.Json;

namespace TaskForge.Tasks.Api.Contracts;

public sealed class TestAssignmentAuthoringRequest : AssignmentMetadataAuthoringRequest
{
    public JsonElement? TestSettings { get; init; }
    public JsonElement? Questions { get; init; }
}
