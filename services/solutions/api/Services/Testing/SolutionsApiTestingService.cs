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
using static TaskForge.Solutions.Api.Services.Serialization.SolutionsApiSerializationService;

namespace TaskForge.Solutions.Api.Services.Testing;

internal static class SolutionsApiTestingService
{
    internal static JsonElement[] ExtractTests(JudgeSpec? spec)
    {
        // Hidden/autograder tests must come only from the task owner service.
        // Client-supplied tests are intentionally ignored here so a user cannot
        // submit a trivial test set and receive Accepted/100 for arbitrary code.
        if (spec?.TestCases is JsonElement tc)
        {
            var arr = ElementToArray(tc);
            if (arr.Length > 0) return arr;
        }

        if (spec?.Tests is JsonElement tests)
        {
            var arr = ElementToArray(tests);
            if (arr.Length > 0) return arr;
        }

        if (!string.IsNullOrWhiteSpace(spec?.TestsJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(spec.TestsJson);
                return ElementToArray(doc.RootElement);
            }
            catch { }
        }

        return [];
    }

    internal static JsonElement NormalizeTestCase(JsonElement item, bool hidden)
    {
        if (item.ValueKind != JsonValueKind.Object) return item.Clone();
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            input = ReadString(item, "input") ?? ReadString(item, "stdin") ?? string.Empty,
            expectedOutput = ReadString(item, "expectedOutput") ?? ReadString(item, "expected") ?? ReadString(item, "stdout") ?? string.Empty,
            isHidden = hidden || ReadBool(item, "isHidden") || ReadBool(item, "hidden")
        }, JsonOptions()));
        return doc.RootElement.Clone();
    }

    internal static void RemoveHiddenTestNodes(JsonNode? node)
    {
        if (node is JsonArray arr)
        {
            for (var i = arr.Count - 1; i >= 0; i--)
            {
                if (IsHiddenTestNode(arr[i])) arr.RemoveAt(i);
                else RemoveHiddenTestNodes(arr[i]);
            }
            return;
        }

        if (node is not JsonObject obj) return;

        foreach (var child in obj.ToList())
        {
            if (IsHiddenTestNode(child.Value)) obj.Remove(child.Key);
            else RemoveHiddenTestNodes(child.Value);
        }
    }

    internal static bool IsHiddenTestNode(JsonNode? node)
        => node is JsonObject obj && (JsonBool(obj, "isHidden") || JsonBool(obj, "hidden"));

}
