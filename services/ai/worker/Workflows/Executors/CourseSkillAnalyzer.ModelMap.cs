using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TaskForge.AiAgent.Contracts;

namespace TaskForge.AiAgent.Workflows.Executors;

internal static partial class CourseSkillAnalyzer
{
    private static HashSet<Guid> CollectAssignmentIds(JsonElement payload)
    {
        var candidates = new List<AssignmentSkillCandidate>();
        CollectAssignmentCandidates(payload, candidates, 0);
        return candidates.Where(x => !x.IsHidden && !x.IsAiDraft).Select(x => x.Id).ToHashSet();
    }

    private static IReadOnlyList<JsonObject> NormalizeBridgePlan(IReadOnlyList<JsonObject> steps)
    {
        var result = new List<JsonObject>();
        foreach (var source in steps)
        {
            var step = source.DeepClone().AsObject();
            var introduced = ReadStringArray(step["introducedSkills"]).ToList();
            var skillSeed = string.Join(" ", introduced.Concat(ReadStringArray(step["titleHint"])).Concat(ReadStringArray(step["reason"])));
            var skillId = step["skillId"]?.ToString();
            if (string.IsNullOrWhiteSpace(skillId)) skillId = NormalizeSkillId(skillSeed);

            step["step"] = result.Count;
            if (!string.IsNullOrWhiteSpace(skillId)) step["skillId"] = skillId;
            step["introducedSkillIds"] = ToSkillIdArray(!string.IsNullOrWhiteSpace(skillId) ? new[] { skillId } : (introduced.Count > 0 ? introduced : new[] { skillSeed }));
            result.Add(step);
        }
        return result;
    }

    private static List<JsonObject> BuildModelNeighborhood(JsonObject root, CourseSkillBridgeContext fallback)
    {
        var result = new List<JsonObject>();
        if (root["courseMap"] is JsonArray map)
        {
            foreach (var item in map.OfType<JsonObject>().Take(24))
            {
                result.Add(new JsonObject
                {
                    ["relativePosition"] = int.TryParse(item["position"]?.ToString(), out var p) ? p : result.Count,
                    ["assignmentId"] = item["assignmentId"]?.ToString(),
                    ["title"] = item["title"]?.ToString(),
                    ["summary"] = item["summary"]?.ToString(),
                    ["requiresSkills"] = ToJsonArray(ReadStringArray(item["requiresSkills"])),
                    ["introducesSkills"] = ToJsonArray(ReadStringArray(item["introducesSkills"])),
                    ["introducedSkillIds"] = ToSkillIdArray(ReadStringArray(item["introducesSkills"])),
                    ["studentHasAfter"] = ToJsonArray(ReadStringArray(item["studentHasAfter"])),
                    ["isRelevantToRequest"] = item["isRelevantToRequest"]?.ToString()
                });
            }
        }

        return result.Count > 0 ? result : fallback.Neighborhood.Select(x => x.DeepClone().AsObject()).ToList();
    }

    private static JsonObject BuildCompactModelMap(JsonObject root)
    {
        var compact = new JsonObject
        {
            ["courseSummary"] = root["courseSummary"]?.DeepClone(),
            ["language"] = root["language"]?.DeepClone(),
            ["warnings"] = root["warnings"]?.DeepClone(),
            ["targetCapability"] = root["targetCapability"]?.DeepClone(),
            ["placementCandidates"] = root["placementCandidates"]?.DeepClone(),
            ["anchor"] = root["anchor"]?.DeepClone()
        };

        if (root["courseMap"] is JsonArray map)
        {
            var arr = new JsonArray();
            foreach (var item in map.OfType<JsonObject>().Take(24))
            {
                arr.Add(new JsonObject
                {
                    ["assignmentId"] = item["assignmentId"]?.ToString(),
                    ["title"] = item["title"]?.ToString(),
                    ["summary"] = item["summary"]?.ToString(),
                    ["introducesSkills"] = ToJsonArray(ReadStringArray(item["introducesSkills"])),
                    ["introducedSkillIds"] = ToSkillIdArray(ReadStringArray(item["introducesSkills"])),
                    ["studentHasAfter"] = ToJsonArray(ReadStringArray(item["studentHasAfter"]))
                });
            }
            compact["courseMap"] = arr;
        }

        return compact;
    }

    private static string? ExtractJson(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var clean = text.Trim();
        if (clean.StartsWith("```", StringComparison.Ordinal))
        {
            clean = Regex.Replace(clean, @"^```[a-zA-Z0-9_-]*\s*", string.Empty);
            clean = Regex.Replace(clean, @"\s*```$", string.Empty).Trim();
        }

        var balanced = ExtractFirstBalancedObject(clean);
        if (!string.IsNullOrWhiteSpace(balanced))
            return balanced;

        var firstObject = clean.IndexOf('{');
        var lastObject = clean.LastIndexOf('}');
        if (firstObject >= 0 && lastObject > firstObject)
            return clean[firstObject..(lastObject + 1)];

        return null;
    }

    private static string? ExtractFirstBalancedObject(string text)
    {
        var start = text.IndexOf('{');
        if (start < 0) return null;

        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var i = start; i < text.Length; i++)
        {
            var ch = text[i];
            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (inString)
            {
                if (ch == '\\') escaped = true;
                else if (ch == '"') inString = false;
                continue;
            }

            if (ch == '"')
            {
                inString = true;
                continue;
            }

            if (ch == '{') depth++;
            else if (ch == '}')
            {
                depth--;
                if (depth == 0)
                    return text[start..(i + 1)];
                if (depth < 0)
                    return null;
            }
        }

        return null;
    }

    private static double? ReadDouble(JsonNode? node)
    {
        return double.TryParse(node?.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    private static Guid? ParseGuid(string? value)
        => Guid.TryParse(value, out var id) ? id : null;


    private static List<string> ReadStringArrayOrFallback(JsonNode? node, IReadOnlyList<string> fallback)
    {
        var values = ReadStringArray(node)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return values.Count > 0 ? values : fallback.ToList();
    }

    private static IEnumerable<string> ReadStringArray(JsonNode? node)
    {
        if (node is JsonArray arr)
        {
            foreach (var item in arr)
            {
                var text = item?.ToString();
                if (!string.IsNullOrWhiteSpace(text)) yield return text.Trim();
            }
        }
        else if (node is not null)
        {
            foreach (var item in node.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!string.IsNullOrWhiteSpace(item)) yield return item;
            }
        }
    }

    private static IEnumerable<JsonObject> ReadObjectArray(JsonNode? node)
    {
        if (node is not JsonArray arr) yield break;
        foreach (var item in arr.OfType<JsonObject>()) yield return item.DeepClone().AsObject();
    }

}
