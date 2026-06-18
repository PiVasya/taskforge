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
using static TaskForge.Tasks.Api.Services.Mapping.AssignmentApiMappingService;
using static TaskForge.Tasks.Api.Services.Math.AssignmentApiMathService;
using static TaskForge.Tasks.Api.Services.Results.AssignmentApiResultsService;
using static TaskForge.Tasks.Api.Services.Serialization.AssignmentApiSerializationService;
using static TaskForge.Tasks.Api.Services.Testing.AssignmentApiTestingService;

namespace TaskForge.Tasks.Api.Services.Image;

internal static partial class AssignmentApiImageService
{
    internal static List<ImageTestCaseSpec> ReadImageTestCases(JsonObject root, string? fallbackInput)
    {
        var list = new List<ImageTestCaseSpec>();
        foreach (var prop in new[] { "testCases", "tests", "cases" })
        {
            if (root[prop] is not JsonArray arr) continue;
            var index = 0;
            foreach (var node in arr)
            {
                index++;
                if (node is not JsonObject o) continue;
                var key = NodeString(o, "expectedImageKey") ?? NodeString(o, "referenceKey") ?? NodeString(o, "imageKey") ?? NodeString(o, "imageTestReferenceKey");
                var image = StripDataUrl(NodeString(o, "expectedImageBase64") ?? NodeString(o, "referenceBase64") ?? NodeString(o, "imageBase64"));
                if (string.IsNullOrWhiteSpace(key) && string.IsNullOrWhiteSpace(image)) continue;
                var threshold = NodeInt(o, "threshold", NodeInt(o, "thresholdPercent", NodeInt(o, "imageTestSimilarityThreshold", JsonNodeInt(root, "imageTestSimilarityThreshold", 90))));
                var contentType = NodeString(o, "expectedImageContentType") ?? NodeString(o, "referenceContentType") ?? "image/png";
                var fileName = NodeString(o, "expectedImageFileName") ?? NodeString(o, "referenceFileName") ?? "expected.png";
                var name = NodeString(o, "name") ?? NodeString(o, "title") ?? $"Тест {index}";
                var input = NodeString(o, "input") ?? NodeString(o, "stdin") ?? string.Empty;
                var expected = NodeString(o, "expectedOutput") ?? NodeString(o, "expected") ?? NodeString(o, "stdout") ?? string.Empty;
                var hidden = NodeBool(o, "isHidden") || NodeBool(o, "hidden");
                list.Add(new ImageTestCaseSpec(name, input, expected, image, key, Math.Clamp(threshold, 0, 100), hidden, contentType, fileName));
            }
            if (list.Count > 0) return list;
        }

        var legacyKey = NodeString(root, "imageTestReferenceKey") ?? NodeString(root, "expectedImageKey") ?? NodeString(root, "referenceKey") ?? NodeString(root, "imageKey");
        var legacy = StripDataUrl(NodeString(root, "referenceBase64") ?? NodeString(root, "expectedImageBase64") ?? NodeString(root, "imageBase64"));
        if (!string.IsNullOrWhiteSpace(legacyKey) || !string.IsNullOrWhiteSpace(legacy))
        {
            var threshold = JsonNodeInt(root, "imageTestSimilarityThreshold", JsonNodeInt(root, "threshold", 90));
            var contentType = NodeString(root, "referenceContentType") ?? NodeString(root, "expectedImageContentType") ?? "image/png";
            var fileName = NodeString(root, "referenceFileName") ?? NodeString(root, "expectedImageFileName") ?? "expected.png";
            var expected = NodeString(root, "expectedOutput") ?? string.Empty;
            list.Add(new ImageTestCaseSpec("Основной тест", fallbackInput ?? string.Empty, expected, legacy, legacyKey, Math.Clamp(threshold, 0, 100), false, contentType, fileName));
        }

        return list;
    }

    internal static string BuildImagePolicyMessage(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("errors", out var errors) || errors.ValueKind != JsonValueKind.Array)
            return "Код содержит запрещённые конструкции.";
        var lines = errors.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.Object)
            .Where(e => !IsSensitiveAnalyzerPattern(StringProp(e, "pattern_id") ?? StringProp(e, "patternId")))
            .Select(e => StringProp(e, "message"))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToArray();
        return lines.Length == 0
            ? "Код использует системные возможности, которые нельзя запускать в песочнице."
            : string.Join("; ", lines);
    }

    internal static bool IsSensitiveAnalyzerPattern(string? patternId)
    {
        var id = (patternId ?? string.Empty).Trim().ToLowerInvariant();
        return id.StartsWith("py.") || id.StartsWith("js.") || id.StartsWith("c.") || id.StartsWith("cpp.") || id.StartsWith("cs.") || id.StartsWith("java.") || id.StartsWith("pas.");
    }

    internal static string NormalizeImageLanguage(string? lang) => (lang ?? "cpp").Trim().ToLowerInvariant() switch
    {
        "c++" or "cpp" or "g++" or "gcc" or "cxx" or "glut" or "cpp-glut" or "c++-glut" or "turtle" or "cpp-turtle" or "c++-turtle" => "cpp",
        "pas" or "pascal" or "pabc" or "graphabc" or "pascalabc" => "pascal",
        "py" or "python" or "python3" or "python-turtle" or "turtle-py" or "matplotlib" or "pillow" => "python",
        var x => x
    };

    internal static string? ImageRunnerService(string lang) => lang switch { "cpp" => "image-cpp-runner", "pascal" => "image-pascal-runner", "python" => "image-python-runner", _ => null };

}
