using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TaskForge.AiAgent.Contracts;

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

    public static IReadOnlyList<string> DetectCanonicalSkillIds(string? text)
    {
        var normalized = NormalizeForSkillId(text);
        if (string.IsNullOrWhiteSpace(normalized)) return Array.Empty<string>();

        var result = new List<string>();
        void AddIf(bool condition, string id)
        {
            if (condition && !result.Contains(id, StringComparer.OrdinalIgnoreCase)) result.Add(id);
        }

        AddIf(ContainsAny(normalized, "tryparse", "try parse", "валидац", "некоррект", "ошибк ввода", "безопасн"), "input-validation");
        AddIf(ContainsAny(normalized, "split", "через пробел", "одной строке", "в одной строке", "раздел", "токен"), "split-input");
        AddIf(ContainsAny(normalized, "две строки", "три строки", "несколько строк", "каждое на отдельной", "последовательн ввод", "нескольких значений"), "multi-line-input");
        AddIf(ContainsAny(normalized, "int.parse", "convert.toint32", "parse", "парсинг", "преобразован", "строки в int", "строку в int", "строку в число", "целое число"), "parse-int");
        AddIf(ContainsAny(normalized, "console.readline", "readline", "stdin", "с клавиатур", "стандартного ввода", "читать входную строку", "прочитай строк", "считай строк", "ввод строк", "входную строку", "ввода данных"), "console-input-line");
        AddIf(ContainsAny(normalized, "console.writeline", "console.write", "stdout", "вывод", "вывести", "напечат"), "console-output");
        AddIf(ContainsAny(normalized, "string.length", "длин", "length"), "string-length");
        AddIf(ContainsAny(normalized, "переменн", "variable", "var ", " int ", " string ", "сохран", "значение"), "variables");
        AddIf(ContainsAny(normalized, "арифмет", "сумм", "слож", "прибав", "вычит", "умнож", "делен", "остат", "+1"), "arithmetic");
        AddIf(ContainsAny(normalized, "услов", "если", "иначе", " if ", " else "), "conditions");
        AddIf(ContainsAny(normalized, "цикл", " for ", " while ", "повтор"), "loops");
        AddIf(ContainsAny(normalized, "массив", "array", "элемент", "индекс"), "arrays");
        AddIf(ContainsAny(normalized, "строков", "литерал", "кавыч", "конкатенац", "интерполяц", "текст"), "strings");
        return result;
    }

    public static string NormalizeSkillId(string? text)
    {
        var direct = NormalizeForSkillId(text).Trim();
        if (string.IsNullOrWhiteSpace(direct)) return string.Empty;
        var detected = DetectCanonicalSkillIds(text);
        if (detected.Count > 0) return detected[0];

        var alias = direct switch
        {
            "input" or "stdin" or "readline" => "console-input-line",
            "output" or "stdout" => "console-output",
            "parse" or "int-parse" or "numeric-parse" => "parse-int",
            "string-input" => "console-input-line",
            _ => null
        };
        if (!string.IsNullOrWhiteSpace(alias)) return alias;

        if (Regex.IsMatch(direct, @"^[a-z][a-z0-9-]{2,}$", RegexOptions.CultureInvariant))
            return direct;

        var slug = Regex.Replace(direct, @"[^a-z0-9а-я+#<>.]+", "-").Trim('-');
        return slug.Length <= 60 ? slug : slug[..60].Trim('-');
    }

    private static JsonArray ToSkillIdArray(IEnumerable<string> values)
    {
        var arr = new JsonArray();
        foreach (var id in values.Select(NormalizeSkillId).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
            arr.Add(id);
        return arr;
    }

    public static JsonObject BuildSkillMapInput(JsonElement payload, string userText, int maxAssignments = 120)
    {
        var root = new JsonObject
        {
            ["userRequest"] = userText,
            ["courseId"] = payload.GetPropertyOrDefault("courseId").ToString(),
            ["course"] = CloneOrNull(payload.GetPropertyOrDefault("course")),
            ["selectedCourse"] = CloneOrNull(payload.GetPropertyOrDefault("courseDigest").GetPropertyOrDefault("selectedCourse")),
            ["targetConcepts"] = CloneOrNull(payload.GetPropertyOrDefault("targetConcepts")),
            ["note"] = "This is the only context the skill-map step should use. Ignore courseCatalog/courseContexts from the full payload. assignments excludes hidden/AI drafts; existingAiDrafts are warnings only and must not count as acquired student skills. Metadata hints are weak and must never be the only placement evidence."
        };

        var assignments = new JsonArray();
        var existingAiDrafts = new JsonArray();
        var seen = new HashSet<Guid>();
        foreach (var item in EnumeratePreferredAssignments(payload))
        {
            if (assignments.Count >= maxAssignments) break;
            var id = GetGuid(item, "id", "assignmentId");
            var title = GetString(item, "title", "name");
            if (!id.HasValue || string.IsNullOrWhiteSpace(title)) continue;
            if (!seen.Add(id.Value)) continue;
            if (LooksLikeCourseCatalogItem(item)) continue;

            var isHidden = GetBool(item, "isHidden") == true;
            var isAiDraft = GetBool(item, "isAiDraft") == true;
            var descriptionPreview = Preview(PlainText(GetString(item, "descriptionPreview", "description", "condition", "body")), 700);
            var row = new JsonObject
            {
                ["assignmentId"] = id.Value.ToString(),
                ["position"] = GetInt(item, "index", "position", "sort") ?? assignments.Count,
                ["sort"] = GetInt(item, "sort", "order"),
                ["title"] = title,
                ["type"] = GetString(item, "type", "assignmentType"),
                ["difficulty"] = GetInt(item, "difficulty"),
                ["rating"] = GetInt(item, "rating"),
                ["tags"] = GetString(item, "tags"),
                ["allowedLanguages"] = CloneOrNull(item.GetPropertyOrDefault("allowedLanguages")),
                ["descriptionPreview"] = descriptionPreview,
                ["assignmentTextForStudy"] = Preview($"{title} {descriptionPreview}", 1200),
                ["weakMetadataConceptHints"] = CloneOrNull(item.GetPropertyOrDefault("conceptHints")),
                ["contentSummary"] = CloneOrNull(item.GetPropertyOrDefault("contentSummary")),
                ["testCases"] = CompactTestCases(item.GetPropertyOrDefault("testCases")),
                ["isHidden"] = isHidden,
                ["isAiDraft"] = isAiDraft
            };

            // AI drafts are useful as dedupe/context warnings, but they must not become
            // the canonical course sequence used for skill-map reasoning. Otherwise the
            // agent learns from its own previous mistakes and rejects/places new drafts
            // based on stale hidden material.
            if (isHidden || isAiDraft)
            {
                existingAiDrafts.Add(new JsonObject
                {
                    ["assignmentId"] = id.Value.ToString(),
                    ["position"] = row["position"]?.DeepClone(),
                    ["title"] = title,
                    ["descriptionPreview"] = descriptionPreview,
                    ["reason"] = isAiDraft ? "existing AI draft; do not count as acquired student skill" : "hidden assignment; do not count as canonical visible course step"
                });
                continue;
            }

            assignments.Add(row);
        }

        root["assignments"] = assignments;
        root["existingAiDrafts"] = existingAiDrafts;
        root["assignmentCount"] = assignments.Count;
        root["fallbackWarning"] = assignments.Count == 0
            ? "No assignments were found in courseDigest/courseOutline/focusAssignments. Do not invent an anchor."
            : null;
        return root;
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
            var bridgePlan = NormalizeBridgePlan(ReadObjectArray(root["bridgePlan"]).ToList()).ToList();
            var confidence = ReadDouble(anchor?["confidence"] ?? root["confidence"] ?? root["anchorConfidence"]);
            if (confidence.HasValue && confidence.Value < 0.6)
            {
                beforeId = null;
                bridgePlan.Clear();
            }

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
