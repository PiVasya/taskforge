using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TaskForge.AiAgent.Contracts;

namespace TaskForge.AiAgent.Workflows.Executors;

internal static partial class CourseSkillAnalyzer
{
    private static string PlainText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var text = value!;
        if (text.TrimStart().StartsWith("{", StringComparison.Ordinal))
        {
            try
            {
                var node = JsonNode.Parse(text);
                var chunks = new List<string>();
                CollectText(node, chunks);
                if (chunks.Count > 0) return string.Join(" ", chunks);
            }
            catch
            {
                // Keep the original text below.
            }
        }
        return text;
    }

    private static void CollectText(JsonNode? node, List<string> chunks)
    {
        if (node is JsonObject obj)
        {
            if (obj.TryGetPropertyValue("text", out var textNode))
            {
                var text = textNode?.ToString();
                if (!string.IsNullOrWhiteSpace(text)) chunks.Add(text);
            }
            foreach (var property in obj)
                CollectText(property.Value, chunks);
        }
        else if (node is JsonArray arr)
        {
            foreach (var item in arr)
                CollectText(item, chunks);
        }
    }

    private static IEnumerable<string> ExtractQueryWords(string text)
    {
        var normalized = Normalize(text);
        var stop = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "сгенерируй", "создай", "сделай", "задачки", "задачи", "задания", "обучалки", "перед", "после", "курс", "основы", "чтобы", "пользователь", "студент", "научился", "научить", "всякое", "программу"
        };

        foreach (var word in Regex.Split(normalized, @"[^a-zа-я0-9_+#<>.]+", RegexOptions.IgnoreCase))
        {
            var clean = word.Trim();
            if (clean.Length >= 4 && !stop.Contains(clean)) yield return clean;
        }
    }

    private static bool MatchesTerm(string normalizedText, string term)
    {
        if (term.StartsWith("re:", StringComparison.Ordinal))
            return Regex.IsMatch(normalizedText, term[3..], RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return normalizedText.Contains(Normalize(term), StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsAny(string text, params string[] needles)
        => needles.Any(needle => text.Contains(Normalize(needle), StringComparison.OrdinalIgnoreCase));

    private static string NormalizeForSkillId(string? value)
    {
        var text = Normalize(value);
        // Common OCR/model mix-ups in Russian words, for example "читaть" with Latin a.
        text = text.Replace("читa", "чита").Replace("считa", "счита");
        text = text.Replace("`", string.Empty);
        return $" {text} ";
    }

    private static string Normalize(string? value)
    {
        var text = (value ?? string.Empty).ToLowerInvariant();
        text = text.Replace("си++", "c++").Replace("с++", "c++").Replace("cpp", "c++");
        text = text.Replace("си#", "c#").Replace("с#", "c#").Replace("c sharp", "c#").Replace("csharp", "c#");
        
        return Regex.Replace(text, @"\s+", " ").Trim();
    }

    private static string Preview(string? value, int max)
    {
        var text = Regex.Replace(value ?? string.Empty, @"\s+", " ").Trim();
        return text.Length <= max ? text : text[..max].TrimEnd() + "…";
    }

    private static JsonArray ToJsonArray(IEnumerable<string> values)
    {
        var arr = new JsonArray();
        foreach (var value in values.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
            arr.Add(value);
        return arr;
    }

    private static JsonArray ToJsonArray(IEnumerable<JsonObject> values)
    {
        var arr = new JsonArray();
        foreach (var value in values) arr.Add(value.DeepClone());
        return arr;
    }
}
