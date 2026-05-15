using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TaskForge.AiAgent.Workflows.Executors;

internal static class CourseSkillAnalyzer
{
    private static readonly SkillRule[] Rules =
    {
        new("program-structure", "каркас программы", new[] { "using system", "public class program", "static void main", "int main", "#include", "namespace" }),
        new("console-output", "вывод на экран", new[] { "console.writeline", "console.write", "cout", "printf", "print", "вывод", "выведите", "напечат" }),
        new("string-literals", "строковые литералы", new[] { "hello", "hi", "строка", "текст", "символ", "кавыч" }),
        new("variables", "переменные", new[] { "переменн", "тип", "значение переменной", @"re:(^|[^a-zа-я0-9_])var\s+", @"re:(^|[^a-zа-я0-9_])int\s+", @"re:(^|[^a-zа-я0-9_])string\s+", @"re:(^|[^a-zа-я0-9_])double\s+", @"re:(^|[^a-zа-я0-9_])bool\s+" }),
        new("arithmetic", "арифметика", new[] { "арифмет", "сумм", "слож", "разност", "произвед", "делен", "остат", "+", "-", "*", "/", "%" }),
        new("comparison", "сравнения", new[] { "сравн", "больше", "меньше", "равн", "не равн", ">", "<", "==", "!=" }),
        new("conditions", "условия", new[] { "re:(^|[^a-zа-я0-9_])if([^a-zа-я0-9_]|$)", "else", "услов", "если", "иначе", "ветв" }),
        new("console-input", "ввод с клавиатуры", new[] { "console.readline", "readline", "cin", "scanf", "input", "stdin", "ввод", "вводится", "введите", "считайте", "считать", "прочитай", "с клавиатур", "стандартного ввода" }),
        new("string-input", "строковый ввод", new[] { "строк", "string", "текст", "слово", "имя", "символ" }),
        new("numeric-parse", "преобразование ввода в число", new[] { "int.parse", "convert.toint32", "parse", "tryparse", "преобраз", "целое число", "число с клавиатуры" }),
        new("multi-line-input", "несколько строк ввода", new[] { "несколько строк", "с новой строки", "каждое число", "две строки", "три строки" }),
        new("split-input", "разбор одной строки на части", new[] { "split", "через пробел", "в одной строке", "одной строке", "раздел", "токен" }),
        new("loops", "циклы", new[] { "цикл", "re:(^|[^a-zа-я0-9_])for([^a-zа-я0-9_]|$)", "re:(^|[^a-zа-я0-9_])while([^a-zа-я0-9_]|$)", "do while", "повтор" }),
        new("arrays", "массивы", new[] { "массив", "array", "элемент", "индекс", "размер массива" }),
        new("collections", "коллекции", new[] { "коллекц", "list<", "dictionary", "map", "set", "vector", "список" }),
        new("methods", "методы и функции", new[] { "метод", "функци", "параметр", "return", "возвращает", "static int", "static string" }),
        new("classes", "классы и объекты", new[] { "класс", "объект", "constructor", "конструктор", "property", "свойств" }),
        new("exceptions", "обработка ошибок", new[] { "tryparse", "try catch", "try-catch", "exception", "исключ", "ошибк" }),
        new("files", "работа с файлами", new[] { "файл", "stream", "reader", "writer", "filesystem" }),
        new("linq", "LINQ", new[] { "linq", "select", "where", "orderby", "lambda", "лямбд" })
    };

    public static CourseSkillBridgeContext Analyze(JsonElement payload, string userText)
    {
        // Neutral fallback only. It intentionally does NOT infer skills or choose
        // an anchor by keyword/regex. The LLM course-skill-map stage is the
        // source of truth for pedagogy. This fallback exists so logs/artifacts
        // can still show the course outline when the LLM map fails.
        var candidates = new List<AssignmentSkillCandidate>();
        CollectAssignmentCandidates(payload, candidates, 0);
        var ordered = candidates
            .Where(x => !x.IsHidden && !x.IsAiDraft)
            .OrderBy(x => x.Index ?? int.MaxValue)
            .ThenBy(x => x.Sort ?? int.MaxValue)
            .ThenBy(x => x.Title, StringComparer.OrdinalIgnoreCase)
            .Select(x => x with { Skills = new List<string>() })
            .ToList();

        var previous = ordered.LastOrDefault();
        return new CourseSkillBridgeContext(
            BeforeAssignmentId: null,
            PreviousTitle: previous?.Title,
            AnchorTitle: null,
            RequestedSkills: Array.Empty<string>(),
            AcquiredSkills: Array.Empty<string>(),
            TargetSkills: Array.Empty<string>(),
            MissingBridgeSkills: Array.Empty<string>(),
            Neighborhood: BuildNeighborhood(ordered, null),
            IsBridgeRequest: LooksLikeLearningBridgeRequest(userText),
            BridgePlan: Array.Empty<JsonObject>(),
            Source: "neutral-fallback",
            AnchorReason: "Structure-only fallback; no keyword/regex skill inference was used.");
    }

    public static bool LooksLikeLearningBridgeRequest(string text)
    {
        var t = Normalize(text);
        return ContainsAny(t, "обучал", "подготов", "перед", "до задач", "мостик", "лестниц", "серия", "набор", "несколько", "guided", "bridge", "prerequisite");
    }

    public static IReadOnlyList<string> DetectSkills(string? text)
    {
        var normalized = Normalize(text);
        if (string.IsNullOrWhiteSpace(normalized)) return Array.Empty<string>();

        return Rules
            .Where(rule => rule.Terms.Any(term => MatchesTerm(normalized, term)))
            .Select(rule => rule.Id)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }



    public static CourseSkillBridgeContext FromModelMap(JsonElement payload, string userText, string modelText, CourseSkillBridgeContext fallback)
    {
        try
        {
            var json = ExtractJson(modelText);
            if (string.IsNullOrWhiteSpace(json))
                return fallback with { Source = "static-fallback", AnchorReason = "LLM course skill map returned no JSON." };

            if (JsonNode.Parse(json) is not JsonObject root)
                return fallback with { Source = "static-fallback", AnchorReason = "LLM course skill map root was not an object." };

            var anchor = root["anchor"] as JsonObject;
            var validAssignmentIds = CollectAssignmentIds(payload);
            var beforeId = ParseGuid(anchor?["insertBeforeAssignmentId"]?.ToString() ?? root["insertBeforeAssignmentId"]?.ToString());
            if (beforeId.HasValue && validAssignmentIds.Count > 0 && !validAssignmentIds.Contains(beforeId.Value))
                beforeId = null;

            var requestedSkills = ReadStringArrayOrFallback(root["requestedSkills"], fallback.RequestedSkills);
            var acquiredSkills = ReadStringArrayOrFallback(root["acquiredSkillsBeforeAnchor"], fallback.AcquiredSkills);
            var targetSkills = ReadStringArrayOrFallback(root["targetSkillsAtAnchor"] ?? root["targetSkills"], fallback.TargetSkills);
            var missingSkills = ReadStringArrayOrFallback(root["missingBridgeSkills"] ?? root["missingSkills"], fallback.MissingBridgeSkills);
            var bridgePlan = ReadObjectArray(root["bridgePlan"]).ToList();

            if (missingSkills.Count == 0 && bridgePlan.Count > 0)
            {
                missingSkills = bridgePlan
                    .SelectMany(step => ReadStringArray(step["introducedSkills"]))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            return new CourseSkillBridgeContext(
                BeforeAssignmentId: beforeId,
                PreviousTitle: anchor?["previousAssignmentTitle"]?.ToString() ?? root["previousAssignmentTitle"]?.ToString() ?? fallback.PreviousTitle,
                AnchorTitle: anchor?["anchorAssignmentTitle"]?.ToString() ?? root["anchorAssignmentTitle"]?.ToString() ?? fallback.AnchorTitle,
                RequestedSkills: requestedSkills,
                AcquiredSkills: acquiredSkills,
                TargetSkills: targetSkills,
                MissingBridgeSkills: missingSkills,
                Neighborhood: BuildModelNeighborhood(root, fallback),
                IsBridgeRequest: fallback.IsBridgeRequest || bridgePlan.Count > 0,
                BridgePlan: bridgePlan,
                Source: "llm-course-skill-map",
                AnchorReason: anchor?["reason"]?.ToString() ?? root["reason"]?.ToString(),
                RawModelMap: BuildCompactModelMap(root));
        }
        catch (Exception ex)
        {
            return fallback with { Source = "static-fallback", AnchorReason = $"LLM course skill map parse failed: {ex.GetType().Name}: {ex.Message}" };
        }
    }

    private static HashSet<Guid> CollectAssignmentIds(JsonElement payload)
    {
        var candidates = new List<AssignmentSkillCandidate>();
        CollectAssignmentCandidates(payload, candidates, 0);
        return candidates.Select(x => x.Id).ToHashSet();
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
            ["warnings"] = root["warnings"]?.DeepClone()
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

        var firstObject = clean.IndexOf('{');
        var lastObject = clean.LastIndexOf('}');
        if (firstObject >= 0 && lastObject > firstObject)
            return clean[firstObject..(lastObject + 1)];

        return null;
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
            if (id.HasValue && !string.IsNullOrWhiteSpace(title) && HasAnyProperty(element, "sort", "index", "type", "assignmentType", "description", "descriptionPreview", "tags"))
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

    private static string Normalize(string? value)
    {
        var text = (value ?? string.Empty).ToLowerInvariant();
        text = text.Replace("си++", "c++").Replace("с++", "c++").Replace("cpp", "c++");
        text = text.Replace("си#", "c#").Replace("с#", "c#").Replace("c sharp", "c#").Replace("csharp", "c#");
        text = text.Replace("питон", "python");
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

    private static string? GetString(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var prop)) continue;
            if (prop.ValueKind == JsonValueKind.String) return prop.GetString();
            if (prop.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False) return prop.ToString();
        }
        return null;
    }

    private static Guid? GetGuid(JsonElement element, params string[] names)
    {
        var raw = GetString(element, names);
        return Guid.TryParse(raw, out var id) ? id : null;
    }

    private static int? GetInt(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var prop)) continue;
            if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var n)) return n;
            if (prop.ValueKind == JsonValueKind.String && int.TryParse(prop.GetString(), out var parsed)) return parsed;
        }
        return null;
    }

    private static bool? GetBool(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var prop)) continue;
            if (prop.ValueKind == JsonValueKind.True) return true;
            if (prop.ValueKind == JsonValueKind.False) return false;
            if (prop.ValueKind == JsonValueKind.String && bool.TryParse(prop.GetString(), out var parsed)) return parsed;
        }
        return null;
    }

    private static bool HasAnyProperty(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object) return false;
        return names.Any(name => element.TryGetProperty(name, out _));
    }

    private sealed record SkillRule(string Id, string Label, string[] Terms);
    private sealed record AssignmentSkillCandidate(Guid Id, int? Index, int? Sort, string Title, string Text, bool IsHidden, bool IsAiDraft, List<string> Skills);
}

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
