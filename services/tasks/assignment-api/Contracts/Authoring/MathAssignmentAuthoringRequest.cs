using System.Text.Json;

namespace TaskForge.Tasks.Api.Contracts;

public sealed class MathAssignmentAuthoringRequest : AssignmentMetadataAuthoringRequest
{
    public JsonElement? TestSettings { get; init; }
    public JsonElement? Blocks { get; init; }
}
