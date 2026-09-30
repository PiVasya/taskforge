using System.Text.Json;

namespace TaskForge.Tasks.Api.Contracts;

public class AssignmentMetadataAuthoringRequest
{
    public Guid? Id { get; init; }
    public string? Title { get; init; }
    public string? Description { get; init; }
    public string? Tags { get; init; }
    public int? Rating { get; init; }
    public bool? IsVisible { get; init; }
    public bool? IsHidden { get; init; }
    public int? Sort { get; init; }
    public JsonElement? AnalyticsSettings { get; init; }
}
