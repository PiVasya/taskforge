using System.Text.Json.Nodes;

namespace TaskForge.AiAgent.Workflows.AgentLoop;

public static partial class CourseAgentTools
{
    private static List<AssignmentSnapshot> GetAssignments(AgentLoopState state)
    {
        var result = new List<AssignmentSnapshot>();
        AddFromMemory(state.WorkingMemory["assignments"], "assignments", result);
        AddFromMemory(state.WorkingMemory["courseOutline"], "courseOutline", result);
        AddFromMemory(state.WorkingMemory["targetAssignments"], "targetAssignments", result);
        AddFromMemory(state.WorkingMemory["focusAssignments"], "focusAssignments", result);
        if (result.Count == 0)
            AddFromMemory(state.WorkingMemory["contextPrompt"], "contextPrompt", result);

        return result
            .GroupBy(x => !string.IsNullOrWhiteSpace(x.Id) ? x.Id! : $"{Normalize(x.Title)}:{x.Index}:{x.Source}")
            .Select(g => g.First())
            .Select((x, index) => x with { Index = index + 1 })
            .ToList();
    }

    private static void AddFromMemory(JsonNode? node, string source, List<AssignmentSnapshot> result)
    {
        switch (node)
        {
            case JsonObject obj:
                CollectAssignments(obj, source, result, 0);
                break;
            case JsonArray arr:
                foreach (var item in arr)
                    AddFromMemory(item, source, result);
                break;
            case JsonValue value:
                var text = value.ToString();
                if (!string.IsNullOrWhiteSpace(text) && (text.Contains("title", StringComparison.OrdinalIgnoreCase) || text.Contains("зад", StringComparison.OrdinalIgnoreCase)))
                {
                    // Text-only context is intentionally not parsed deeply. It is still kept in AgentState for the LLM.
                }
                break;
        }
    }

    private static void CollectAssignments(JsonObject obj, string source, List<AssignmentSnapshot> result, int depth)
    {
        if (result.Count >= 500 || depth > 10) return;

        if (LooksLikeAssignment(obj))
        {
            result.Add(new AssignmentSnapshot(
                result.Count + 1,
                source,
                GetString(obj, "id", "assignmentId"),
                GetString(obj, "title", "name") ?? "Без названия",
                GetString(obj, "assignmentType", "type") ?? "assignment",
                GetString(obj, "language", "defaultLanguage"),
                GetInt(obj, "difficulty", "level"),
                GetInt(obj, "rating", "order", "sortOrder"),
                GetTags(obj),
                GetString(obj, "description", "descriptionPreview", "body", "condition", "text") ?? string.Empty,
                CountArray(obj["publicTests"]) + CountOpenTestCases(obj["testCases"]),
                CountArray(obj["hiddenTests"]) + CountHiddenTestCases(obj["testCases"]),
                !string.IsNullOrWhiteSpace(GetString(obj, "starterCode", "templateCode")),
                !string.IsNullOrWhiteSpace(GetString(obj, "referenceSolution", "solution", "answer")),
                obj));
        }

        foreach (var child in obj)
        {
            if (child.Value is JsonObject childObj)
                CollectAssignments(childObj, source, result, depth + 1);
            else if (child.Value is JsonArray childArr)
            {
                foreach (var item in childArr)
                    if (item is JsonObject itemObj)
                        CollectAssignments(itemObj, source, result, depth + 1);
            }
        }
    }

    private static bool LooksLikeAssignment(JsonObject obj)
    {
        var title = GetString(obj, "title", "name");
        if (string.IsNullOrWhiteSpace(title)) return false;
        var hasAssignmentField = obj.ContainsKey("description")
                                 || obj.ContainsKey("assignmentType")
                                 || obj.ContainsKey("type")
                                 || obj.ContainsKey("testCases")
                                 || obj.ContainsKey("publicTests")
                                 || obj.ContainsKey("hiddenTests")
                                 || obj.ContainsKey("starterCode")
                                 || obj.ContainsKey("referenceSolution")
                                 || obj.ContainsKey("difficulty")
                                 || obj.ContainsKey("rating");
        var hasCourseOnlyFields = obj.ContainsKey("assignments") && !obj.ContainsKey("description") && !obj.ContainsKey("assignmentType");
        return hasAssignmentField && !hasCourseOnlyFields;
    }

}
