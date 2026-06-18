using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using TaskForge.Tasks.Api.Data;
using TaskForge.Tasks.Api.Domain;

using TaskForge.Tasks.Api.Contracts;
using static TaskForge.Tasks.Api.Services.Access.AssignmentApiAccessService;
using static TaskForge.Tasks.Api.Services.Common.AssignmentApiCommonService;
using static TaskForge.Tasks.Api.Services.Image.AssignmentApiImageService;
using static TaskForge.Tasks.Api.Services.Mapping.AssignmentApiMappingService;
using static TaskForge.Tasks.Api.Services.Math.AssignmentApiMathService;
using static TaskForge.Tasks.Api.Services.Results.AssignmentApiResultsService;
using static TaskForge.Tasks.Api.Services.Testing.AssignmentApiTestingService;

namespace TaskForge.Tasks.Api.Services.Serialization;

internal static class AssignmentApiSerializationService
{
    internal static int JsonNodeInt(JsonObject o, string name, int fallback) => int.TryParse(NodeString(o, name), out var v) ? v : fallback;

    internal static TaskSpec ReadTaskSpec(Assignment assignment)
    {
        var root = JsonNode.Parse(string.IsNullOrWhiteSpace(assignment.TestsJson) ? "{}" : assignment.TestsJson!) as JsonObject ?? new JsonObject();
        return ParseTaskSpec(root);
    }

    internal static void NormalizeIds(JsonObject root, string arrayName)
    {
        if (root[arrayName] is not JsonArray arr) return;
        foreach (var item in arr.OfType<JsonObject>())
        {
            var raw = item["id"]?.GetValue<string>();
            if (!Guid.TryParse(raw, out var id) || id == Guid.Empty) item["id"] = Guid.NewGuid().ToString();
        }
    }

    internal static object ToImportDto(Assignment x) => new
    {
        id = x.Id,
        courseId = x.CourseId,
        type = x.Type,
        title = x.Title,
        description = x.Description,
        language = x.Language,
        allowedLanguages = ParseCsv(x.AllowedLanguagesCsv, x.Language),
        tags = x.Tags ?? string.Empty,
        difficulty = x.Difficulty,
        rating = x.Rating,
        sort = x.Sort,
        starterCode = x.StarterCode,
        tests = ParseJson(x.TestsJson),
        testsJson = x.TestsJson,
        codeForbiddenCalls = ParseStringArrayJson(x.CodeForbiddenCallsJson),
        codeRequiredCalls = ParseStringArrayJson(x.CodeRequiredCallsJson),
        isVisible = x.IsVisible,
        isHidden = !x.IsVisible,
        imageTestReferenceKey = JsonString(x.TestsJson, "imageTestReferenceKey"),
        imageTestSimilarityThreshold = JsonInt(x.TestsJson, "imageTestSimilarityThreshold", 90)
    };

    internal static bool HasMeaningfulJsonText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        try
        {
            using var doc = JsonDocument.Parse(text);
            return HasMeaningfulJsonValue(doc.RootElement);
        }
        catch { return true; }
    }

    internal static bool HasMeaningfulJsonElement(JsonElement? element) => element.HasValue && HasMeaningfulJsonValue(element.Value);

    internal static bool HasMeaningfulJsonValue(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Array => element.GetArrayLength() > 0,
        JsonValueKind.Object => element.EnumerateObject().Any(),
        JsonValueKind.Null or JsonValueKind.Undefined => false,
        _ => true
    };

    internal static string NormalizeAssignmentType(string? value)
    {
        var s = (value ?? string.Empty).Trim().ToLowerInvariant();
        return s switch
        {
            "code" or "code_test" or "codetest" or "programming" or "programming-test" => "code-test",
            "image" or "image_test" or "imagetest" or "drawing" or "drawing-test" => "image-test",
            "quiz" or "task-test" or "multiple-choice" => "test",
            "math-test" or "math_task" or "math-task" => "math",
            "image-test" or "code-test" or "test" or "math" => s,
            _ => "code-test"
        };
    }

    internal static string? NormalizeSpecJsonForStorage(string? testsJson, string type)
    {
        if (string.IsNullOrWhiteSpace(testsJson)) return testsJson;
        if (type != "test" && type != "math") return testsJson;
        try
        {
            var node = JsonNode.Parse(testsJson) as JsonObject;
            if (node == null) return testsJson;
            NormalizeIds(node, type == "test" ? "questions" : "blocks");
            return node.ToJsonString(JsonOptions());
        }
        catch
        {
            return testsJson;
        }
    }

    internal static IEnumerable<JsonElement> ExtractAssignmentImportItems(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object) yield return item;
            }
            yield break;
        }

        if (root.ValueKind != JsonValueKind.Object) yield break;

        foreach (var name in new[] { "assignments", "items", "tasks" })
        {
            if (root.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in arr.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object) yield return item;
                }
                yield break;
            }
        }

        if (root.TryGetProperty("assignment", out var nested) && nested.ValueKind == JsonValueKind.Object)
        {
            yield return nested;
            yield break;
        }

        yield return root;
    }

    internal static AssignmentRequest AssignmentRequestFromJson(JsonElement source)
    {
        if (source.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("ожидался JSON-объект");
        }

        var type = FirstString(source, "type", "kind", "assignmentType");
        var tests = PickTestsElement(source, NormalizeAssignmentType(type));

        return new AssignmentRequest(
            FirstGuid(source, "id", "assignmentId"),
            FirstString(source, "title", "name", "assignmentTitle"),
            FirstString(source, "description", "statement", "condition", "body", "prompt"),
            type,
            FirstString(source, "language", "defaultLanguage"),
            FirstStringList(source, "allowedLanguages", "languages"),
            FirstString(source, "tags"),
            FirstInt(source, "difficulty", "level"),
            FirstInt(source, "rating", "score", "points"),
            FirstString(source, "starterCode", "templateCode", "initialCode"),
            FirstString(source, "testsJson"),
            tests,
            tests,
            FirstStringList(source, "codeForbiddenCalls", "forbiddenCalls", "forbidden"),
            FirstStringList(source, "codeRequiredCalls", "requiredCalls", "required"),
            FirstBool(source, "isVisible", "visible"),
            FirstBool(source, "isHidden", "hidden"),
            FirstInt(source, "sort", "order"),
            FirstString(source, "imageTestReferenceKey", "referenceKey", "expectedImageKey"),
            FirstInt(source, "imageTestSimilarityThreshold", "similarityThreshold", "threshold")
        );
    }

    internal static IEnumerable<string> ValidateImportedAssignment(AssignmentRequest request, int index)
    {
        var isPatch = request.Id.HasValue && request.Id.Value != Guid.Empty;
        var hasExplicitType = !string.IsNullOrWhiteSpace(request.Type);
        var title = (request.Title ?? string.Empty).Trim();
        if (!isPatch && title.Length == 0) yield return "title обязателен.";
        if (title.Length > 200) yield return "title не должен быть длиннее 200 символов.";
        var type = NormalizeAssignmentType(request.Type);
        if (hasExplicitType && !new[] { "code-test", "image-test", "test", "math" }.Contains(type, StringComparer.OrdinalIgnoreCase)) yield return "type должен быть code-test, image-test, test или math.";
        if (request.Difficulty.HasValue && (request.Difficulty.Value < 1 || request.Difficulty.Value > 3)) yield return "difficulty должен быть 1, 2 или 3.";
        if (request.Rating.HasValue && request.Rating.Value < 0) yield return "rating не может быть отрицательным.";
        if (request.Sort.HasValue && request.Sort.Value < 0) yield return "sort не может быть отрицательным.";
        var mustValidateSpec = !isPatch || hasExplicitType;
        if (mustValidateSpec && (type == "code-test" || type == "image-test") && string.IsNullOrWhiteSpace(request.TestsJson) && !request.Tests.HasValue && !request.TestCases.HasValue) yield return "для code-test/image-test желательно указать testCases/tests.";
        if (mustValidateSpec && (type == "test" || type == "math") && string.IsNullOrWhiteSpace(request.TestsJson) && !request.Tests.HasValue && !request.TestCases.HasValue) yield return "для test/math нужно указать spec/questions/blocks.";
    }

    internal static string? NormalizeLanguage(string? value)
    {
        var s = (value ?? string.Empty).Trim().ToLowerInvariant();
        return s switch
        {
            "c#" or "cs" or "csharp" => "csharp",
            "c++" or "cpp" or "g++" or "gcc" or "cxx" => "cpp",
            "py" or "python" or "python3" => "python",
            "js" or "node" or "nodejs" or "node.js" or "javascript" => "javascript",
            "pas" or "pascal" or "pascalabc" or "pascalabcnet" or "pabc" => "pascal",
            "java" => "java",
            _ => null
        };
    }

    internal static List<string> NormalizeLanguageList(IEnumerable<string>? values)
    {
        var list = (values ?? []).Select(NormalizeLanguage).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return list.Count == 0 ? [] : list.Where(x => SupportedCodeLanguages().Contains(x, StringComparer.OrdinalIgnoreCase)).ToList();
    }

    internal static string? NormalizeLanguagesCsv(IEnumerable<string>? values)
    {
        var list = NormalizeLanguageList(values);
        return list.Count == 0 ? null : string.Join(',', list);
    }

    internal static string[] ParseCsv(string? csv, string fallback)
    {
        var list = NormalizeLanguageList((csv ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (list.Count > 0) return list.ToArray();
        var one = NormalizeLanguage(fallback) ?? "csharp";
        return [one];
    }

    internal static string? StringArrayJson(IEnumerable<string>? values)
    {
        if (values == null) return null;
        var list = values.Select(x => (x ?? string.Empty).Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return list.Length == 0 ? null : JsonSerializer.Serialize(list, JsonOptions());
    }

    internal static string[] ParseStringArrayJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<string[]>(json, JsonOptions())?.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() ?? [];
        }
        catch { return []; }
    }

    internal static string? RawJson(JsonElement? value) => value.HasValue ? value.Value.GetRawText() : null;

    internal static object? ParseJson(string? json) { if (string.IsNullOrWhiteSpace(json)) return null; try { return JsonSerializer.Deserialize<JsonElement>(json); } catch { return json; } }

    internal static JsonElement? ParseJsonElement(string? json) { if (string.IsNullOrWhiteSpace(json)) return null; try { return JsonSerializer.Deserialize<JsonElement>(json); } catch { return null; } }

    internal static object JsonPropArray(object? json, string name) { if (json is JsonElement e && e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Array) return p; return Array.Empty<object>(); }

    internal static List<Guid> ParseGuidList(string? json) { try { return string.IsNullOrWhiteSpace(json) ? [] : JsonSerializer.Deserialize<List<Guid>>(json, JsonOptions()) ?? []; } catch { return []; } }

    internal static string? JsonString(string? json, string name) { var e = ParseJsonElement(json); return e.HasValue && e.Value.ValueKind == JsonValueKind.Object && e.Value.TryGetProperty(name, out var p) ? p.ToString() : null; }

    internal static int JsonInt(string? json, string name, int fallback) { var e = ParseJsonElement(json); return e.HasValue && e.Value.ValueKind == JsonValueKind.Object && e.Value.TryGetProperty(name, out var p) && p.TryGetInt32(out var v) ? v : fallback; }

    internal static JsonSerializerOptions JsonOptions() => new(JsonSerializerDefaults.Web) { WriteIndented = false };

    internal static TaskSpec ParseTaskSpec(JsonObject root)
    {
        var settings = root["settings"] as JsonObject ?? new JsonObject();
        var questions = root["questions"] as JsonArray ?? new JsonArray();
        return new TaskSpec(
            new TestSettings(
                Int(settings, "maxAttempts", 1),
                System.Math.Clamp(Int(settings, "passPercent", 60), 0, 100),
                Bool(settings, "shuffleQuestions", true),
                Bool(settings, "shuffleAnswers", true),
                Bool(settings, "allowReview", true),
                IntList(settings, "attemptTimeLimitsSeconds")),
            questions.OfType<JsonObject>().Select(ParseQuestion).OrderBy(x => x.Order).ToList());
    }

    internal static JsonObject TaskSpecToJsonObject(TaskSpec spec) => new()
    {
        ["settings"] = JsonSerializer.SerializeToNode(spec.Settings, JsonOptions()),
        ["questions"] = JsonSerializer.SerializeToNode(spec.Questions, JsonOptions())
    };

    internal static TestQuestion ParseQuestion(JsonObject q) => new(
        GuidV(q, "id"),
        Int(q, "order", 0),
        Str(q, "type", "single-choice"),
        Str(q, "prompt", ""),
        Options(q),
        StringList(q, "correctOptionKeys"),
        StringList(q, "acceptedAnswers"),
        Bool(q, "caseSensitive", false),
        Bool(q, "trim", true));

    internal static MathBlock ParseBlock(JsonObject b) => new(
        GuidV(b, "id"),
        Int(b, "order", 0),
        Str(b, "kind", "info"),
        Str(b, "prompt", ""),
        b["promptContentJson"]?.GetValue<string>(),
        Int(b, "score", 1),
        Bool(b, "isRequired", true),
        Options(b),
        StringList(b, "correctOptionKeys"),
        StringList(b, "acceptedAnswers"),
        Bool(b, "caseSensitive", false),
        Bool(b, "trim", true),
        DoubleN(b, "numericTolerance"),
        StringList(b, "orderItems"),
        Options(b, "matchLeftItems"),
        Options(b, "matchRightItems"),
        MatchPairList(b, "matchPairs"));

}
