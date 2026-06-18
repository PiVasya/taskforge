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
    internal static async Task<JsonObject> MergeAndMaterializeImageTestPayloadAsync(string? existingJson, AssignmentRequest request, Guid assignmentId, IHttpClientFactory clients, IConfiguration cfg, CancellationToken ct)
    {
        var node = MergeImageTestPayload(existingJson, request);
        if (assignmentId != Guid.Empty)
        {
            await MaterializeImageTestImagesAsync(node, assignmentId, clients, cfg, ct);
        }
        return node;
    }

    internal static async Task MaterializeImageTestImagesAsync(JsonObject root, Guid assignmentId, IHttpClientFactory clients, IConfiguration cfg, CancellationToken ct)
    {
        foreach (var arrayName in new[] { "testCases", "tests", "cases", "publicTests", "hiddenTests" })
        {
            if (root[arrayName] is not JsonArray arr) continue;
            var index = 0;
            foreach (var item in arr.OfType<JsonObject>())
            {
                index++;
                await MaterializeImageObjectAsync(item, assignmentId, index, clients, cfg, ct);
            }
        }

        var rootKey = NodeString(root, "imageTestReferenceKey") ?? NodeString(root, "expectedImageKey") ?? NodeString(root, "referenceKey");
        var rootBase64 = NodeString(root, "referenceBase64") ?? NodeString(root, "expectedImageBase64") ?? NodeString(root, "imageBase64");
        if (string.IsNullOrWhiteSpace(rootKey) && !string.IsNullOrWhiteSpace(rootBase64))
        {
            var decoded = DecodeImageBase64(rootBase64, NodeString(root, "referenceContentType") ?? NodeString(root, "expectedImageContentType") ?? "image/png");
            var uploaded = await UploadImageBytesToFilesApiAsync(clients, cfg, decoded.Bytes, NodeString(root, "referenceFileName") ?? NodeString(root, "expectedImageFileName") ?? $"reference-{assignmentId:N}{ExtensionForContentType(decoded.ContentType)}", decoded.ContentType, $"image-tests/reference/{assignmentId:N}", ct);
            root["imageTestReferenceKey"] = uploaded.Key;
            root["imageTestReferenceUrl"] = uploaded.PrivateUrl;
            root["referenceFileName"] = uploaded.FileName;
            root["referenceContentType"] = uploaded.ContentType;
            root["referenceSize"] = uploaded.Size;
        }
        RemoveInlineImageFields(root);
    }

    internal static async Task MaterializeImageObjectAsync(JsonObject o, Guid assignmentId, int index, IHttpClientFactory clients, IConfiguration cfg, CancellationToken ct)
    {
        var key = NodeString(o, "expectedImageKey") ?? NodeString(o, "referenceKey") ?? NodeString(o, "imageKey") ?? NodeString(o, "imageTestReferenceKey");
        if (!string.IsNullOrWhiteSpace(key))
        {
            o["expectedImageKey"] = key;
            o["expectedImageUrl"] = PrivateFileUrl(key);
            RemoveInlineImageFields(o);
            return;
        }

        var raw = NodeString(o, "expectedImageBase64") ?? NodeString(o, "referenceBase64") ?? NodeString(o, "imageBase64");
        if (string.IsNullOrWhiteSpace(raw)) return;

        var decoded = DecodeImageBase64(raw, NodeString(o, "expectedImageContentType") ?? NodeString(o, "referenceContentType") ?? "image/png");
        var uploaded = await UploadImageBytesToFilesApiAsync(clients, cfg, decoded.Bytes, NodeString(o, "expectedImageFileName") ?? NodeString(o, "referenceFileName") ?? $"case-{index}{ExtensionForContentType(decoded.ContentType)}", decoded.ContentType, $"image-tests/reference/{assignmentId:N}", ct);
        o["expectedImageKey"] = uploaded.Key;
        o["expectedImageUrl"] = uploaded.PrivateUrl;
        o["expectedImageContentType"] = uploaded.ContentType;
        o["expectedImageFileName"] = uploaded.FileName;
        o["expectedImageSize"] = uploaded.Size;
        RemoveInlineImageFields(o);
    }

    internal static void RemoveInlineImageFields(JsonObject o)
    {
        o.Remove("expectedImageBase64");
        o.Remove("referenceBase64");
        o.Remove("imageBase64");
        o.Remove("expectedBase64");
    }

    internal static (byte[] Bytes, string ContentType) DecodeImageBase64(string raw, string fallbackContentType)
    {
        var text = (raw ?? string.Empty).Trim();
        var contentType = string.IsNullOrWhiteSpace(fallbackContentType) ? "image/png" : fallbackContentType;
        if (text.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = text.IndexOf(',');
            if (comma > 0)
            {
                var meta = text[5..comma];
                var semi = meta.IndexOf(';');
                if (semi >= 0) meta = meta[..semi];
                if (!string.IsNullOrWhiteSpace(meta)) contentType = meta;
                text = text[(comma + 1)..];
            }
        }
        return (Convert.FromBase64String(text), contentType);
    }

    internal static string ExtensionForContentType(string? contentType) => (contentType ?? string.Empty).ToLowerInvariant() switch
    {
        "image/jpeg" or "image/jpg" => ".jpg",
        "image/webp" => ".webp",
        "image/gif" => ".gif",
        "image/bmp" => ".bmp",
        "image/svg+xml" => ".svg",
        _ => ".png"
    };

    internal static JsonObject MergeImageTestPayload(string? existingJson, AssignmentRequest request)
    {
        JsonObject node;
        try
        {
            var parsed = JsonNode.Parse(string.IsNullOrWhiteSpace(existingJson) ? "{}" : existingJson!);
            if (parsed is JsonObject obj)
            {
                node = obj;
            }
            else if (parsed is JsonArray arr)
            {
                node = new JsonObject { ["testCases"] = arr.DeepClone() };
            }
            else
            {
                node = new JsonObject();
            }
        }
        catch
        {
            node = new JsonObject();
        }
        if (!string.IsNullOrWhiteSpace(request.ImageTestReferenceKey)) node["imageTestReferenceKey"] = request.ImageTestReferenceKey;
        if (request.ImageTestSimilarityThreshold.HasValue) node["imageTestSimilarityThreshold"] = Math.Clamp(request.ImageTestSimilarityThreshold.Value, 0, 100);
        return node;
    }

    internal static JsonElement WrapImageTests(JsonElement source)
    {
        var node = JsonNode.Parse(WrapCodeTests(source).GetRawText()) as JsonObject ?? new JsonObject();
        foreach (var name in new[] { "imageTestReferenceKey", "imageTestSimilarityThreshold", "referenceKey", "threshold" })
        {
            if (source.TryGetProperty(name, out var v)) node[name] = JsonNode.Parse(v.GetRawText());
        }
        return JsonSerializer.SerializeToElement(node, JsonOptions());
    }

}
