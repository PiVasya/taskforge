using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TaskForge.Shared.Import;

// Arrays and string bytes are significant; JSON object property order is not.
public static class ImportJsonIdentity
{
    public static string Hash(JsonElement value)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer)) WriteCanonical(writer, value);
        return Convert.ToHexString(SHA256.HashData(buffer.ToArray())).ToLowerInvariant();
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in value.EnumerateObject().OrderBy(x => x.Name, StringComparer.Ordinal))
            {
                writer.WritePropertyName(property.Name);
                WriteCanonical(writer, property.Value);
            }
            writer.WriteEndObject();
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var item in value.EnumerateArray()) WriteCanonical(writer, item);
            writer.WriteEndArray();
        }
        else value.WriteTo(writer);
    }

    public static JsonElement Read(string json) => JsonSerializer.Deserialize<JsonElement>(json);

    public static JsonElement ReserveMissingIds(JsonElement graph)
    {
        var node = JsonNode.Parse(graph.GetRawText());
        if (node is not JsonObject && node is not JsonArray) return graph.Clone();
        // Legacy wrappers share the same reservation guarantee, without changing
        // the canonical format or accepting malformed explicit identities.
        var canonical = node is JsonObject document && (document.ContainsKey("schemaVersion") || document.ContainsKey("format"));
        var lists = new List<JsonArray>();
        if (node is JsonArray array) lists.Add(array);
        else if (node is JsonObject objRoot)
        {
            var names = canonical ? new[] { "courses", "tasks" } : new[] { "assignments", "items", "tasks" };
            foreach (var name in names)
            {
                if (objRoot[name] is not JsonArray list) continue;
                lists.Add(list);
                if (!canonical) break;
            }
            if (!canonical && lists.Count == 0)
            {
                var item = objRoot["assignment"] as JsonObject ?? objRoot;
                if (item["id"] is null) item["id"] = Guid.NewGuid().ToString("D");
            }
        }
        var assigned = false;
        foreach (var list in lists)
        {
            if (list is null) continue;
            foreach (var item in list.OfType<JsonObject>())
            {
                // Do not turn malformed explicit IDs into valid input.
                if (item["id"] is not null) continue;
                item["id"] = Guid.NewGuid().ToString("D");
                assigned = true;
            }
        }
        if (assigned && canonical && node is JsonObject obj && obj["scopes"] is JsonArray scopes
            && !scopes.Any(x => x?.GetValue<string>() == "ids")) scopes.Add("ids");
        return JsonSerializer.SerializeToElement(node);
    }
}
