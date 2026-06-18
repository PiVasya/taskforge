using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using TaskForge.Solutions.Api.Data;
using TaskForge.Solutions.Api.Domain;

using TaskForge.Solutions.Api.Contracts;
using static TaskForge.Solutions.Api.Services.Access.SolutionsApiAccessService;
using static TaskForge.Solutions.Api.Services.Common.SolutionsApiCommonService;
using static TaskForge.Solutions.Api.Services.Image.SolutionsApiImageService;
using static TaskForge.Solutions.Api.Services.Mapping.SolutionsApiMappingService;
using static TaskForge.Solutions.Api.Services.Results.SolutionsApiResultsService;
using static TaskForge.Solutions.Api.Services.Testing.SolutionsApiTestingService;

namespace TaskForge.Solutions.Api.Services.Serialization;

internal static class SolutionsApiSerializationService
{
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

    internal static string? ReadString(JsonElement item, string name) => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var v) ? v.ToString() : null;

    internal static bool ReadBool(JsonElement item, string name) => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False && v.GetBoolean();

    internal static JsonElement? ExtractResults(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        foreach (var name in new[] { "results", "testCases", "cases" })
        {
            if (root.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.Array)
            {
                return prop.Clone();
            }
        }
        return null;
    }

    internal static JsonElement? CloneJson(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try
        {
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.Clone();
        }
        catch
        {
            return null;
        }
    }

    internal static Guid? TryReadGuid(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out var prop)) return null;
        return Guid.TryParse(prop.ToString(), out var id) ? id : null;
    }

    internal static string NormalizeStatusKey(string? value) => string.Join(string.Empty, (value ?? string.Empty).Where(char.IsLetterOrDigit)).ToLowerInvariant();

    internal static string NormalizeSearch(string? value) => string.Join(' ', (value ?? string.Empty).Trim().ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    internal static bool JsonBool(JsonObject obj, string name)
        => obj.TryGetPropertyValue(name, out var node) && node is JsonValue value && value.TryGetValue<bool>(out var b) && b;

    internal static object? ParseJson(string? json) => ParseJsonElement(json);

    internal static JsonElement? ParseJsonElement(string? json) { if (string.IsNullOrWhiteSpace(json)) return null; try { using var doc = JsonDocument.Parse(json); return doc.RootElement.Clone(); } catch { return null; } }

    internal static string? TryReadString(JsonElement? element, string name)
    {
        if (!element.HasValue || element.Value.ValueKind != JsonValueKind.Object) return null;
        var e = element.Value;
        if (!e.TryGetProperty(name, out var prop)) return null;
        var value = prop.ToString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    internal static double? TryReadNumber(JsonElement? element, string name)
    {
        if (!element.HasValue || element.Value.ValueKind != JsonValueKind.Object) return null;
        var e = element.Value;
        if (!e.TryGetProperty(name, out var prop)) return null;
        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetDouble(out var number)) return number;
        if (double.TryParse(prop.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed)) return parsed;
        return null;
    }

    internal static JsonSerializerOptions JsonOptions() => new(JsonSerializerDefaults.Web) { WriteIndented = false };

}
