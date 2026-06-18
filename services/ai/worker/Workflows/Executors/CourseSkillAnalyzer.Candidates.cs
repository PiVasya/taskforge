using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TaskForge.AiAgent.Contracts;

namespace TaskForge.AiAgent.Workflows.Executors;

internal static partial class CourseSkillAnalyzer
{
    private static AssignmentSkillCandidate? FindAnchor(IReadOnlyList<AssignmentSkillCandidate> ordered, IReadOnlyList<string> requestedSkills, string userText)
    {
        if (ordered.Count == 0) return null;

        if (requestedSkills.Count > 0)
        {
            var direct = ordered.FirstOrDefault(x => x.Skills.Any(skill => requestedSkills.Contains(skill, StringComparer.OrdinalIgnoreCase)));
            if (direct != null) return direct;
        }

        var queryWords = ExtractQueryWords(userText).ToList();
        if (queryWords.Count > 0)
        {
            var lexical = ordered.FirstOrDefault(x => queryWords.Any(word => Normalize(x.Text).Contains(word, StringComparison.OrdinalIgnoreCase)));
            if (lexical != null) return lexical;
        }

        return null;
    }

    private static List<JsonObject> BuildNeighborhood(IReadOnlyList<AssignmentSkillCandidate> ordered, AssignmentSkillCandidate? anchor)
    {
        if (ordered.Count == 0) return new List<JsonObject>();
        var anchorIndex = anchor == null ? Math.Max(0, ordered.Count - 1) : ordered.ToList().FindIndex(x => x.Id == anchor.Id);
        if (anchorIndex < 0) anchorIndex = 0;

        var start = Math.Max(0, anchorIndex - 4);
        var end = Math.Min(ordered.Count - 1, anchorIndex + 4);
        var result = new List<JsonObject>();
        for (var i = start; i <= end; i++)
        {
            var item = ordered[i];
            result.Add(new JsonObject
            {
                ["relativePosition"] = i - anchorIndex,
                ["title"] = item.Title,
                ["skills"] = ToJsonArray(item.Skills),
                ["preview"] = Preview(item.Text, 280)
            });
        }
        return result;
    }

    private static void CollectAssignmentCandidates(JsonElement element, List<AssignmentSkillCandidate> result, int depth)
    {
        if (depth > 10) return;
        if (element.ValueKind == JsonValueKind.Object)
        {
            var id = GetGuid(element, "id", "assignmentId");
            var title = GetString(element, "title", "name");
            var description = GetString(element, "description", "descriptionPreview", "condition", "body");
            var tags = GetString(element, "tags");
            if (id.HasValue && !string.IsNullOrWhiteSpace(title) && IsAssignmentLike(element))
            {
                var text = $"{title} {description} {tags}";
                result.Add(new AssignmentSkillCandidate(
                    id.Value,
                    GetInt(element, "index"),
                    GetInt(element, "sort", "order"),
                    title!,
                    text,
                    GetBool(element, "isHidden") == true,
                    GetBool(element, "isAiDraft") == true,
                    DetectSkills(text).ToList()));
            }

            foreach (var prop in element.EnumerateObject())
                CollectAssignmentCandidates(prop.Value, result, depth + 1);
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                CollectAssignmentCandidates(item, result, depth + 1);
        }
    }


    private static IEnumerable<JsonElement> EnumeratePreferredAssignments(JsonElement payload)
    {
        var selectedCourseId = GetCurrentCourseId(payload);
        foreach (var item in EnumerateArray(payload.GetPropertyOrDefault("courseDigest").GetPropertyOrDefault("assignments"))) yield return item;
        foreach (var item in EnumerateArray(payload.GetPropertyOrDefault("courseOutline"))) yield return item;
        foreach (var item in EnumerateArray(payload.GetPropertyOrDefault("focusAssignments"))) yield return item;
        foreach (var item in EnumerateArray(payload.GetPropertyOrDefault("targetAssignments"))) yield return item;
        foreach (var item in EnumerateArray(payload.GetPropertyOrDefault("assignments"))) yield return item;
        foreach (var context in EnumerateArray(payload.GetPropertyOrDefault("courseContexts")))
        {
            var contextCourseId = GetGuid(context, "courseId", "id")
                                  ?? GetGuid(context.GetPropertyOrDefault("course"), "id")
                                  ?? GetGuid(context.GetPropertyOrDefault("selectedCourse"), "id");
            if (selectedCourseId.HasValue && contextCourseId.HasValue && selectedCourseId.Value != contextCourseId.Value)
                continue;

            foreach (var item in EnumerateArray(context.GetPropertyOrDefault("assignments"))) yield return item;
        }
    }

    private static Guid? GetCurrentCourseId(JsonElement payload)
    {
        return GetGuid(payload, "courseId")
               ?? GetGuid(payload.GetPropertyOrDefault("course"), "id")
               ?? GetGuid(payload.GetPropertyOrDefault("courseDigest").GetPropertyOrDefault("selectedCourse"), "id");
    }

    private static IEnumerable<JsonElement> EnumerateArray(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array) yield break;
        foreach (var item in element.EnumerateArray()) yield return item;
    }

    private static JsonNode? CloneOrNull(JsonElement element)
    {
        if (element.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return null;
        try { return JsonNode.Parse(element.GetRawText()); }
        catch { return element.ToString(); }
    }

    private static JsonArray CompactTestCases(JsonElement testCases)
    {
        var arr = new JsonArray();
        foreach (var tc in EnumerateArray(testCases).Take(4))
        {
            arr.Add(new JsonObject
            {
                ["input"] = Preview(GetString(tc, "input"), 160),
                ["expectedOutput"] = Preview(GetString(tc, "expectedOutput"), 160),
                ["isHidden"] = GetBool(tc, "isHidden") == true
            });
        }
        return arr;
    }

    private static bool LooksLikeCourseCatalogItem(JsonElement element)
    {
        return HasAnyProperty(element, "isPublic", "assignmentCount") && !HasAnyProperty(element, "courseId", "assignmentType", "descriptionPreview", "conceptHints", "testCases", "allowedLanguages");
    }

    private static bool IsAssignmentLike(JsonElement element)
    {
        if (LooksLikeCourseCatalogItem(element)) return false;
        return HasAnyProperty(element, "courseId", "sort", "index", "type", "assignmentType", "descriptionPreview", "tags", "conceptHints", "testCases", "allowedLanguages", "isAiDraft");
    }

}
