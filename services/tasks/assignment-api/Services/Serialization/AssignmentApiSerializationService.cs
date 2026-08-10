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

    internal static JsonObject ToImportDto(Assignment x)
    {
        var type = NormalizeAssignmentType(x.Type);
        var testsPayload = ExportTestsPayload(x);
        var testsJson = x.TestsJson;
        var codeForbiddenCalls = ParseStringArrayJson(x.CodeForbiddenCallsJson);
        var codeRequiredCalls = ParseStringArrayJson(x.CodeRequiredCallsJson);
        var allowedLanguages = ParseCsv(x.AllowedLanguagesCsv, x.Language);

        var obj = new JsonObject
        {
            ["id"] = x.Id.ToString(),
            ["type"] = type,
            ["title"] = x.Title,
            ["description"] = x.Description ?? string.Empty,
            ["language"] = x.Language,
            ["allowedLanguages"] = JsonSerializer.SerializeToNode(allowedLanguages, JsonOptions()),
            ["tags"] = x.Tags ?? string.Empty,
            ["difficulty"] = x.Difficulty,
            ["rating"] = x.Rating,
            ["starterCode"] = x.StarterCode ?? string.Empty,
            ["isVisible"] = x.IsVisible
        };

        if (type is "code-test" or "image-test")
        {
            var imageThreshold = type == "image-test" ? JsonInt(testsJson, "imageTestSimilarityThreshold", 90) : (int?)null;
            obj["testCases"] = ExportTestCasesNode(x.TestsJson, imageThreshold);
            obj["codeForbiddenCalls"] = JsonSerializer.SerializeToNode(codeForbiddenCalls, JsonOptions());
            obj["codeRequiredCalls"] = JsonSerializer.SerializeToNode(codeRequiredCalls, JsonOptions());
        }
        else if (type == "test")
        {
            var spec = CloneJsonNode(testsPayload) as JsonObject ?? new JsonObject();
            obj["testSettings"] = CloneJsonNode(spec["settings"]) ?? new JsonObject();
            obj["questions"] = CloneJsonNode(spec["questions"]) ?? new JsonArray();
        }
        else if (type == "math")
        {
            var spec = CloneJsonNode(testsPayload) as JsonObject ?? new JsonObject();
            obj["testSettings"] = CloneJsonNode(spec["settings"]) ?? new JsonObject();
            obj["blocks"] = CloneJsonNode(spec["blocks"]) ?? new JsonArray();
        }

        if (type == "image-test")
        {
            var referenceKey = JsonString(testsJson, "imageTestReferenceKey");
            if (!string.IsNullOrWhiteSpace(referenceKey)) obj["imageTestReferenceKey"] = referenceKey;
            obj["imageTestSimilarityThreshold"] = JsonInt(testsJson, "imageTestSimilarityThreshold", 90);
        }

        return obj;
    }

    internal static object? ExportTestsPayload(Assignment x)
    {
        var type = NormalizeAssignmentType(x.Type);
        return type switch
        {
            "test" => TaskSpecToJsonObject(ReadTaskSpec(x)),
            "math" => MathSpecToJsonObject(ReadMathSpec(x)),
            _ => ParseJson(x.TestsJson)
        };
    }

    internal static JsonNode ExportTestCasesNode(string? json, int? defaultImageThreshold = null)
    {
        if (string.IsNullOrWhiteSpace(json)) return new JsonArray();
        try
        {
            var parsed = JsonNode.Parse(json);
            if (parsed is JsonArray arr) return NormalizeExportTestCasesArray(arr, defaultImageThreshold);
            if (parsed is JsonObject obj)
            {
                foreach (var name in new[] { "testCases", "cases", "tests" })
                {
                    if (obj[name] is JsonArray nested) return NormalizeExportTestCasesArray(nested, defaultImageThreshold);
                }

                var combined = new JsonArray();
                if (obj["publicTests"] is JsonArray publicTests)
                {
                    foreach (var item in publicTests) combined.Add(JsonNode.Parse(item?.ToJsonString(JsonOptions()) ?? "null"));
                }
                if (obj["hiddenTests"] is JsonArray hiddenTests)
                {
                    foreach (var item in hiddenTests)
                    {
                        var clone = JsonNode.Parse(item?.ToJsonString(JsonOptions()) ?? "null");
                        if (clone is JsonObject hiddenObj) hiddenObj["isHidden"] = true;
                        combined.Add(clone);
                    }
                }
                return NormalizeExportTestCasesArray(combined, defaultImageThreshold);
            }
        }
        catch { }
        return new JsonArray();
    }

    internal static JsonArray NormalizeExportTestCasesArray(JsonArray source, int? defaultImageThreshold = null)
    {
        var result = new JsonArray();
        foreach (var item in source)
        {
            var clone = JsonNode.Parse(item?.ToJsonString(JsonOptions()) ?? "null");
            if (defaultImageThreshold.HasValue && clone is JsonObject obj)
            {
                RemoveThresholdIfDefault(obj, "threshold", defaultImageThreshold.Value);
                RemoveThresholdIfDefault(obj, "thresholdPercent", defaultImageThreshold.Value);
                RemoveThresholdIfDefault(obj, "imageTestSimilarityThreshold", defaultImageThreshold.Value);
            }
            result.Add(clone);
        }
        return result;
    }

    internal static void RemoveThresholdIfDefault(JsonObject obj, string name, int defaultValue)
    {
        if (obj[name] == null) return;
        if (int.TryParse(obj[name]!.ToString(), out var value) && value == defaultValue) obj.Remove(name);
    }

    internal static string? ExportTestsJson(Assignment x, object? testsPayload)
    {
        if (testsPayload is JsonNode node) return node.ToJsonString(JsonOptions());
        return x.TestsJson;
    }

    internal static object? ExportInteractiveSettingsPayload(Assignment x, object? testsPayload)
    {
        var type = NormalizeAssignmentType(x.Type);
        if (type is not ("test" or "math")) return null;
        if (testsPayload is JsonObject obj && obj["settings"] is JsonObject settings) return JsonNode.Parse(settings.ToJsonString(JsonOptions()));
        if (testsPayload is JsonElement e && e.ValueKind == JsonValueKind.Object && e.TryGetProperty("settings", out var settingsElement)) return settingsElement;
        return null;
    }

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

    internal static bool HasImportShapeFields(JsonElement source)
    {
        if (source.ValueKind != JsonValueKind.Object) return false;
        foreach (var name in new[] { "testCases", "questions", "blocks", "testSettings", "imageTestReferenceKey", "imageTestSimilarityThreshold", "tests", "testSpec", "mathSpec" })
        {
            if (TryGetPropertyLoose(source, name, out _)) return true;
        }
        return false;
    }

    internal static string InferAssignmentTypeFromJson(JsonElement source, string? explicitType)
    {
        if (!string.IsNullOrWhiteSpace(explicitType)) return NormalizeAssignmentType(explicitType);
        if (source.ValueKind != JsonValueKind.Object) return "code-test";

        if (TryGetPropertyLoose(source, "blocks", out var blocks) && blocks.ValueKind == JsonValueKind.Array) return "math";
        if (TryGetPropertyLoose(source, "mathSettings", out var mathSettings) && mathSettings.ValueKind == JsonValueKind.Object) return "math";
        if (TryGetPropertyLoose(source, "mathSpec", out var mathSpec) && mathSpec.ValueKind is JsonValueKind.Object or JsonValueKind.Array) return "math";
        if (TryGetPropertyLoose(source, "tests", out var nestedTestsForMath) && nestedTestsForMath.ValueKind == JsonValueKind.Object && TryGetPropertyLoose(nestedTestsForMath, "blocks", out var nestedBlocks) && nestedBlocks.ValueKind == JsonValueKind.Array) return "math";

        if (TryGetPropertyLoose(source, "questions", out var questions) && questions.ValueKind == JsonValueKind.Array) return "test";
        if (TryGetPropertyLoose(source, "testSettings", out var testSettings) && testSettings.ValueKind == JsonValueKind.Object) return "test";
        if (TryGetPropertyLoose(source, "quizSettings", out var quizSettings) && quizSettings.ValueKind == JsonValueKind.Object) return "test";
        if (TryGetPropertyLoose(source, "testSpec", out var testSpec) && testSpec.ValueKind is JsonValueKind.Object or JsonValueKind.Array) return "test";
        if (TryGetPropertyLoose(source, "tests", out var nestedTestsForTest) && nestedTestsForTest.ValueKind == JsonValueKind.Object && TryGetPropertyLoose(nestedTestsForTest, "questions", out var nestedQuestions) && nestedQuestions.ValueKind == JsonValueKind.Array) return "test";

        if (TryGetPropertyLoose(source, "imageTestReferenceKey", out _) || TryGetPropertyLoose(source, "imageTestSimilarityThreshold", out _)) return "image-test";
        if (TryGetPropertyLoose(source, "testCases", out var testCases) && ContainsImageExpectedFields(testCases)) return "image-test";

        return "code-test";
    }

    internal static bool ContainsImageExpectedFields(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var name in new[] { "expectedImageKey", "expectedImageBase64", "expectedImageUrl", "referenceKey", "referenceBase64", "imageKey", "imageBase64" })
            {
                if (TryGetPropertyLoose(element, name, out _)) return true;
            }
            foreach (var name in new[] { "testCases", "cases", "tests" })
            {
                if (TryGetPropertyLoose(element, name, out var nested) && ContainsImageExpectedFields(nested)) return true;
            }
        }
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (ContainsImageExpectedFields(item)) return true;
            }
        }
        return false;
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

    internal static string? MergeInteractiveSpecJsonForStorage(string? existingJson, string? incomingJson, string type)
    {
        if (type != "test" && type != "math") return NormalizeSpecJsonForStorage(incomingJson, type);
        if (string.IsNullOrWhiteSpace(incomingJson)) return existingJson;

        try
        {
            var incoming = JsonNode.Parse(incomingJson) as JsonObject;
            if (incoming == null) return NormalizeSpecJsonForStorage(incomingJson, type);

            JsonObject result;
            try
            {
                result = JsonNode.Parse(string.IsNullOrWhiteSpace(existingJson) ? "{}" : existingJson!) as JsonObject ?? new JsonObject();
            }
            catch
            {
                result = new JsonObject();
            }

            var arrayName = type == "math" ? "blocks" : "questions";
            if (incoming["settings"] is JsonObject settings) result["settings"] = settings.DeepClone();
            if (incoming[arrayName] is JsonArray arr) result[arrayName] = arr.DeepClone();
            NormalizeIds(result, arrayName);
            return result.ToJsonString(JsonOptions());
        }
        catch
        {
            return NormalizeSpecJsonForStorage(incomingJson, type);
        }
    }

    internal static string[] ArrayAliasesForSpec(string arrayName) => arrayName switch
    {
        "testCases" => ["testCases", "cases", "tests", "publicTests", "hiddenTests"],
        "questions" => ["questions"],
        "blocks" => ["blocks"],
        _ => [arrayName]
    };

    internal static bool HasJsonArray(string? json, string arrayName)
    {
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            var node = JsonNode.Parse(json);
            if (node is JsonArray arr) return arr.Count > 0;
            if (node is JsonObject obj)
            {
                foreach (var name in ArrayAliasesForSpec(arrayName))
                {
                    if (obj[name] is JsonArray nested && nested.Count > 0) return true;
                }
            }
        }
        catch { }
        return false;
    }

    internal static bool RequestHasArrayPayload(AssignmentRequest request, string arrayName)
    {
        foreach (var element in new[] { request.Tests, request.TestCases })
        {
            if (!element.HasValue) continue;
            var e = element.Value;
            if (e.ValueKind == JsonValueKind.Array && e.GetArrayLength() > 0) return true;
            if (e.ValueKind == JsonValueKind.Object)
            {
                foreach (var name in ArrayAliasesForSpec(arrayName))
                {
                    if (TryGetPropertyLoose(e, name, out var arr) && arr.ValueKind == JsonValueKind.Array && arr.GetArrayLength() > 0) return true;
                }
            }
        }
        return HasJsonArray(request.TestsJson, arrayName);
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

        var explicitType = FirstString(source, "type", "kind", "assignmentType");
        var inferredType = InferAssignmentTypeFromJson(source, explicitType);
        var type = !string.IsNullOrWhiteSpace(explicitType) ? explicitType : (HasImportShapeFields(source) ? inferredType : null);
        var normalizedType = NormalizeAssignmentType(type ?? inferredType);
        var tests = PickTestsElement(source, normalizedType);
        var testsJson = tests.HasValue ? null : FirstString(source, "testsJson");
        var testsForRequest = normalizedType is "test" or "math" ? tests : null;
        var testCasesForRequest = normalizedType is "code-test" or "image-test" ? tests : null;

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
            testsJson,
            testsForRequest,
            testCasesForRequest,
            FirstStringList(source, "codeForbiddenCalls", "forbiddenCalls", "forbidden"),
            FirstStringList(source, "codeRequiredCalls", "requiredCalls", "required"),
            FirstBool(source, "isVisible", "visible"),
            FirstBool(source, "isHidden", "hidden"),
            FirstInt(source, "sort", "order"),
            FirstString(source, "imageTestReferenceKey", "referenceKey", "expectedImageKey"),
            FirstInt(source, "imageTestSimilarityThreshold", "similarityThreshold", "threshold"),
            FirstElement(source, "analyticsSettings", "assignmentAnalyticsSettings", "analytics")
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
        var mustValidateSpec = !isPatch;
        if (mustValidateSpec && (type == "code-test" || type == "image-test") && !RequestHasArrayPayload(request, "testCases")) yield return "для code-test/image-test нужно указать непустой testCases.";
        if (mustValidateSpec && type == "test" && !RequestHasArrayPayload(request, "questions")) yield return "для test нужно указать непустой questions, настройки — в testSettings.";
        if (mustValidateSpec && type == "math" && !RequestHasArrayPayload(request, "blocks")) yield return "для math нужно указать непустой blocks, настройки — в testSettings.";
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

    internal static JsonNode? ParseJsonNode(string? json) { if (string.IsNullOrWhiteSpace(json)) return null; try { return JsonNode.Parse(json); } catch { return null; } }

    internal static JsonNode? CloneJsonNode(object? value)
    {
        if (value == null) return null;
        return value switch
        {
            JsonNode node => JsonNode.Parse(node.ToJsonString(JsonOptions())),
            JsonElement element => JsonNode.Parse(element.GetRawText()),
            _ => JsonSerializer.SerializeToNode(value, JsonOptions())
        };
    }

    internal static JsonElement? ParseJsonElement(string? json) { if (string.IsNullOrWhiteSpace(json)) return null; try { return JsonSerializer.Deserialize<JsonElement>(json); } catch { return null; } }

    internal static object JsonPropArray(object? json, string name)
    {
        if (json is JsonElement e && e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Array) return p;
        if (json is JsonObject o && o[name] is JsonArray arr) return (object?)JsonNode.Parse(arr.ToJsonString(JsonOptions())) ?? Array.Empty<object>();
        return Array.Empty<object>();
    }

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
