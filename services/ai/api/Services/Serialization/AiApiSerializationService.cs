using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using TaskForge.Ai.Api.Data;
using TaskForge.Ai.Api.Domain;

using TaskForge.Ai.Api.Contracts;
using TaskForge.Ai.Api.Hubs;
using static TaskForge.Ai.Api.Services.Access.AiApiAccessService;
using static TaskForge.Ai.Api.Services.Common.AiApiCommonService;
using static TaskForge.Ai.Api.Services.Mapping.AiApiMappingService;
using static TaskForge.Ai.Api.Services.Results.AiApiResultsService;
using static TaskForge.Ai.Api.Services.Testing.AiApiTestingService;

namespace TaskForge.Ai.Api.Services.Serialization;

internal static class AiApiSerializationService
{
    internal static object BuildPatchPreview(JsonObject data)
    {
        var patches = data["patches"] as JsonArray ?? new JsonArray();
        return new
        {
            patchCount = patches.Count,
            field = data["field"]?.ToString(),
            operation = data["operation"]?.ToString(),
            patches = patches.OfType<JsonObject>().Take(200).Select(p => new
            {
                assignmentId = p["assignmentId"]?.ToString(),
                title = p["title"]?.ToString(),
                changes = p["changes"],
                diff = p["diff"]
            }).ToArray()
        };
    }

    internal static Guid? GuidFromNode(JsonNode? node) => Guid.TryParse(node?.ToString(), out var id) ? id : null;

    internal static Guid? GuidFromPayload(JsonElement payload, string name) => payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(name, out var v) && Guid.TryParse(v.ToString(), out var id) ? id : null;

    internal static JsonSerializerOptions JsonOptions() => new(JsonSerializerDefaults.Web) { WriteIndented = false };

    internal static IEnumerable<TaskForge.Ai.Api.Domain.AiArtifact> ExtractArtifacts(Guid runId, Guid conversationId, JsonElement? result)
    {
        if (!result.HasValue || result.Value.ValueKind != JsonValueKind.Object) yield break;
        JsonElement root = result.Value;
        if (root.TryGetProperty("result", out var nested) && nested.ValueKind == JsonValueKind.Object) root = nested;
        if (!root.TryGetProperty("artifacts", out var artifacts) || artifacts.ValueKind != JsonValueKind.Array) yield break;
        foreach (var item in artifacts.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var type = item.TryGetProperty("type", out var t) ? t.ToString() : "artifact";
            var title = item.TryGetProperty("title", out var ti) ? ti.ToString() : type;
            var data = item.TryGetProperty("data", out var d) ? d.GetRawText() : item.GetRawText();
            yield return new TaskForge.Ai.Api.Domain.AiArtifact { RunId = runId, ConversationId = conversationId, Type = string.IsNullOrWhiteSpace(type) ? "artifact" : type, Title = string.IsNullOrWhiteSpace(title) ? "Материал ассистента" : title, DataJson = string.IsNullOrWhiteSpace(data) ? "{}" : data };
        }
    }

    internal static object? ParseJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<JsonElement>(json); }
        catch { return null; }
    }

    internal static JsonNode? ParseJsonNode(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonNode.Parse(json); }
        catch { return null; }
    }

    internal static string ExtractAssistantMessage(JsonElement? result)
    {
        if (!result.HasValue || result.Value.ValueKind != JsonValueKind.Object) return "Готово.";
        if (result.Value.TryGetProperty("assistantMessage", out var m) && m.ValueKind == JsonValueKind.String) return m.GetString() ?? "Готово.";
        if (result.Value.TryGetProperty("message", out var directMessage) && directMessage.ValueKind == JsonValueKind.String) return directMessage.GetString() ?? "Готово.";
        if (result.Value.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.Object)
        {
            if (r.TryGetProperty("assistantMessage", out var rm) && rm.ValueKind == JsonValueKind.String) return rm.GetString() ?? "Готово.";
            if (r.TryGetProperty("message", out var nestedMessage) && nestedMessage.ValueKind == JsonValueKind.String) return nestedMessage.GetString() ?? "Готово.";
        }
        return "Готово.";
    }

    internal static string ReadMessageText(JsonElement payload)
    {
        if (payload.ValueKind == JsonValueKind.String) return (payload.GetString() ?? string.Empty).Trim();
        if (payload.ValueKind != JsonValueKind.Object) return string.Empty;

        foreach (var name in new[] { "text", "content", "message", "prompt" })
        {
            if (payload.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String)
            {
                return (v.GetString() ?? string.Empty).Trim();
            }
        }

        if (payload.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object)
        {
            foreach (var name in new[] { "text", "content" })
            {
                if (message.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String)
                {
                    return (v.GetString() ?? string.Empty).Trim();
                }
            }
        }

        return string.Empty;
    }

    internal static async Task<JsonNode?> FetchJsonOrWarningAsync(HttpClient client, string url, string label, JsonArray warnings, CancellationToken ct)
    {
        try
        {
            using var response = await client.GetAsync(url, ct);
            var raw = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                warnings.Add(new JsonObject
                {
                    ["source"] = label,
                    ["status"] = (int)response.StatusCode,
                    ["message"] = "Контекст не получен от связанного сервиса."
                });
                return null;
            }
            if (string.IsNullOrWhiteSpace(raw)) return null;
            return JsonNode.Parse(raw);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            warnings.Add(new JsonObject
            {
                ["source"] = label,
                ["message"] = ex.Message
            });
            return null;
        }
    }

    internal static JsonArray? ExtractArray(JsonNode? node)
    {
        if (node is JsonArray direct) return direct;
        if (node is not JsonObject obj) return null;
        foreach (var key in new[] { "assignments", "items", "data", "rows", "tasks" })
        {
            if (obj[key] is JsonArray array) return array;
        }
        return null;
    }

    internal static string ReadStepString(JsonElement? step, string name, string fallback)
    {
        if (!step.HasValue || step.Value.ValueKind != JsonValueKind.Object) return fallback;
        return step.Value.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? fallback
            : fallback;
    }

    internal static bool ReadStepBool(JsonElement? step, string name, bool fallback)
    {
        if (!step.HasValue || step.Value.ValueKind != JsonValueKind.Object) return fallback;
        return step.Value.TryGetProperty(name, out var v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False)
            ? v.GetBoolean()
            : fallback;
    }

}
