using System.Text.Json.Nodes;

namespace TaskForge.AiAgent.Workflows.AgentLoop;

public static partial class CourseAgentTools
{
    private static int SearchScore(AssignmentSnapshot item, string query, List<string> concepts)
    {
        var score = 0;
        var haystack = Normalize(string.Join(" ", item.Title, item.Description, item.Type, item.Language, string.Join(" ", item.Tags)));
        if (!string.IsNullOrWhiteSpace(query) && haystack.Contains(query, StringComparison.OrdinalIgnoreCase)) score += 10;
        foreach (var concept in concepts)
            if (haystack.Contains(Normalize(concept), StringComparison.OrdinalIgnoreCase)) score += 3;
        return score;
    }

    private static IEnumerable<string> ExtractConcepts(AssignmentSnapshot item)
        => ExtractConcepts(string.Join(" ", item.Title, item.Description, item.Type, item.Language, string.Join(" ", item.Tags)));

    private static IEnumerable<string> ExtractConcepts(string text)
    {
        var lower = Normalize(text);
        foreach (var keyword in ConceptKeywords)
        {
            if (lower.Contains(Normalize(keyword), StringComparison.OrdinalIgnoreCase))
                yield return keyword;
        }
    }

    private static string? GetString(JsonObject obj, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!obj.TryGetPropertyValue(key, out var node) || node is null) continue;
            if (node is JsonValue) return node.ToString();
            if (node is JsonArray arr) return string.Join(", ", arr.Select(x => x?.ToString()).Where(x => !string.IsNullOrWhiteSpace(x)));
        }
        return null;
    }

    private static int? GetInt(JsonObject obj, params string[] keys)
    {
        var raw = GetString(obj, keys);
        if (int.TryParse(raw, out var value)) return value;
        if (double.TryParse(raw, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var number)) return (int)Math.Round(number);
        return null;
    }

    private static List<string> GetTags(JsonObject obj)
    {
        if (!obj.TryGetPropertyValue("tags", out var node) || node is null)
            return new List<string>();
        if (node is JsonArray arr)
            return arr.Select(x => x?.ToString()).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return node.ToString().Split(new[] { ',', ';', '|', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static int CountOpenTestCases(JsonNode? node)
    {
        if (node is not JsonArray arr) return 0;
        return arr.Count(x => x is JsonObject obj && !ReadBool(obj["isHidden"]));
    }

    private static int CountHiddenTestCases(JsonNode? node)
    {
        if (node is not JsonArray arr) return 0;
        return arr.Count(x => x is JsonObject obj && ReadBool(obj["isHidden"]));
    }

    private static int CountArray(JsonNode? node) => node is JsonArray arr ? arr.Count : 0;

    private static bool ReadBool(JsonNode? node)
    {
        if (node is null) return false;
        return bool.TryParse(node.ToString(), out var value) && value;
    }

    private static bool ContainsAny(string text, params string[] needles)
    {
        return needles.Any(x => text.Contains(x, StringComparison.OrdinalIgnoreCase));
    }

    private static bool StartsWithNumber(string text)
    {
        var trimmed = text.TrimStart();
        return trimmed.Length > 0 && char.IsDigit(trimmed[0]);
    }

    private static double Ratio(int part, int total) => total <= 0 ? 0 : Math.Round((double)part / total, 3);
    private static double Average(List<int> values) => values.Count == 0 ? 0 : Math.Round(values.Average(), 2);
    private static string Normalize(string value) => (value ?? string.Empty).Trim().ToLowerInvariant();
    private static string Trim(string? value, int maxLength)
    {
        var text = value ?? string.Empty;
        return text.Length <= maxLength ? text : text[..maxLength] + "...";
    }

    private static bool LooksLikeDraftRequest(string text)
    {
        var t = Normalize(text);
        return (t.Contains("создай") || t.Contains("сгенер") || t.Contains("придум") || t.Contains("сделай") || t.Contains("накидай"))
               && (t.Contains("задач") || t.Contains("задани") || t.Contains("курс"));
    }
}
