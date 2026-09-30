using System.Text.Json;

namespace TaskForge.Tasks.Api.Contracts;

public sealed class CodeAssignmentAuthoringRequest : AssignmentMetadataAuthoringRequest
{
    public string? Language { get; init; }
    public List<string>? AllowedLanguages { get; init; }
    public string? StarterCode { get; init; }
    public JsonElement? TestCases { get; init; }
    public List<string>? CodeForbiddenCalls { get; init; }
    public List<string>? CodeRequiredCalls { get; init; }
}
