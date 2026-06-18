using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TaskForge.AiAgent.Contracts;

namespace TaskForge.AiAgent.Workflows.Executors;

public sealed record CourseSkillBridgeContext(
    Guid? BeforeAssignmentId,
    string? PreviousTitle,
    string? AnchorTitle,
    IReadOnlyList<string> RequestedSkills,
    IReadOnlyList<string> AcquiredSkills,
    IReadOnlyList<string> TargetSkills,
    IReadOnlyList<string> MissingBridgeSkills,
    IReadOnlyList<JsonObject> Neighborhood,
    bool IsBridgeRequest,
    IReadOnlyList<JsonObject>? BridgePlan = null,
    string Source = "static",
    string? AnchorReason = null,
    JsonObject? RawModelMap = null)
{
    public JsonObject ToJsonObject()
    {
        static JsonArray ToStringArray(IEnumerable<string> values)
        {
            var arr = new JsonArray();
            foreach (var value in values.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
                arr.Add(value);
            return arr;
        }

        var neighborhood = new JsonArray();
        foreach (var item in Neighborhood) neighborhood.Add(item.DeepClone());

        var bridgePlan = new JsonArray();
        foreach (var item in BridgePlan ?? Array.Empty<JsonObject>()) bridgePlan.Add(item.DeepClone());

        return new JsonObject
        {
            ["insertBeforeAssignmentId"] = BeforeAssignmentId?.ToString(),
            ["previousAssignmentTitle"] = PreviousTitle,
            ["anchorAssignmentTitle"] = AnchorTitle,
            ["requestedSkills"] = ToStringArray(RequestedSkills),
            ["acquiredSkillsBeforeAnchor"] = ToStringArray(AcquiredSkills),
            ["targetSkillsAtAnchor"] = ToStringArray(TargetSkills),
            ["missingBridgeSkills"] = ToStringArray(MissingBridgeSkills),
            ["neighborhood"] = neighborhood,
            ["bridgePlan"] = bridgePlan,
            ["source"] = Source,
            ["anchorReason"] = AnchorReason,
            ["rawModelMap"] = RawModelMap?.DeepClone(),
            ["isBridgeRequest"] = IsBridgeRequest
        };
    }
}
