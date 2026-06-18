using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Text.Json.Serialization;

namespace TaskForge.Execution.Worker;

public sealed partial class Worker
{
    private static JsonElement[] ParseTests(string? testsJson)
    {
        if (string.IsNullOrWhiteSpace(testsJson)) return [];
        try
        {
            using var doc = JsonDocument.Parse(testsJson);
            return ElementToArray(doc.RootElement);
        }
        catch
        {
            return [];
        }
    }

    private static JsonElement[] ElementToArray(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Array) return element.EnumerateArray().Select(x => x.Clone()).ToArray();
        if (element.ValueKind == JsonValueKind.Object)
        {
            var merged = new List<JsonElement>();
            if (element.TryGetProperty("publicTests", out var publicTests) && publicTests.ValueKind == JsonValueKind.Array)
                merged.AddRange(publicTests.EnumerateArray().Select(x => NormalizeTestCase(x, hidden: false)));
            if (element.TryGetProperty("hiddenTests", out var hiddenTests) && hiddenTests.ValueKind == JsonValueKind.Array)
                merged.AddRange(hiddenTests.EnumerateArray().Select(x => NormalizeTestCase(x, hidden: true)));
            if (merged.Count > 0) return merged.ToArray();

            foreach (var name in new[] { "testCases", "tests", "cases" })
            {
                if (element.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.Array)
                {
                    return prop.EnumerateArray().Select(x => x.Clone()).ToArray();
                }
            }
        }
        return [];
    }

    private static JsonElement NormalizeTestCase(JsonElement item, bool hidden)
    {
        if (item.ValueKind != JsonValueKind.Object) return item.Clone();
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            input = ReadString(item, "input") ?? ReadString(item, "stdin") ?? string.Empty,
            expectedOutput = ReadString(item, "expectedOutput") ?? ReadString(item, "expected") ?? ReadString(item, "stdout") ?? string.Empty,
            isHidden = hidden || ReadBool(item, "isHidden") || ReadBool(item, "hidden")
        }, JsonOptions));
        return doc.RootElement.Clone();
    }

    private static bool ReadBool(JsonElement item, string name)
        => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False && value.GetBoolean();


}
