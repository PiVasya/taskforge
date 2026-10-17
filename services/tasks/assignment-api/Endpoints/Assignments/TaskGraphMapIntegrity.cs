using System.Text.Json;
using TaskForge.Shared.Import;

namespace TaskForge.Tasks.Api.Endpoints;

// Compares the immutable data-import receipt with the exact map document
// submitted for the map stage. This prevents a partial graph from being
// acknowledged as a fully completed import.
internal static class TaskGraphMapIntegrity
{
    internal static IReadOnlyList<string> MissingConnections(JsonElement dataReceipt, JsonElement document, Guid rootCourseId)
    {
        var missing = new List<string>();
        if (dataReceipt.ValueKind != JsonValueKind.Object
            || !dataReceipt.TryGetProperty("taskGraph", out var graph) || graph.ValueKind != JsonValueKind.Object
            || !graph.TryGetProperty("apply", out var apply) || apply.ValueKind != JsonValueKind.Object
            || !apply.TryGetProperty("connections", out var enabled) || enabled.ValueKind != JsonValueKind.True)
            return missing;
        if (document.ValueKind != JsonValueKind.Object || !document.TryGetProperty("nodes", out var nodes)
            || nodes.ValueKind != JsonValueKind.Array || !document.TryGetProperty("edges", out var edges)
            || edges.ValueKind != JsonValueKind.Array)
            return ["$.document.nodes/edges"];

        var entityByKey = new Dictionary<string, Guid>(StringComparer.Ordinal) { ["$course"] = rootCourseId };
        if (graph.TryGetProperty("courses", out var courses) && courses.ValueKind == JsonValueKind.Array)
            foreach (var course in courses.EnumerateArray())
                if (course.ValueKind == JsonValueKind.Object && GetString(course, "key") is { Length: > 0 } key
                    && Guid.TryParse(GetString(course, "id"), out var id)) entityByKey[key] = id;
        if (graph.TryGetProperty("tasks", out var tasks) && tasks.ValueKind == JsonValueKind.Array)
            foreach (var task in tasks.EnumerateArray())
                if (task.ValueKind == JsonValueKind.Object && GetString(task, "key") is { Length: > 0 } key
                    && Guid.TryParse(GetString(task, "assignmentId"), out var id)) entityByKey[key] = id;

        var nodeByEntity = new Dictionary<Guid, HashSet<string>>();
        foreach (var node in nodes.EnumerateArray())
        {
            if (node.ValueKind != JsonValueKind.Object || !Guid.TryParse(GetString(node, "entityId"), out var entityId)) continue;
            var id = GetString(node, "id");
            if (id is null) continue;
            if (!nodeByEntity.TryGetValue(entityId, out var ids)) nodeByEntity[entityId] = ids = new HashSet<string>(StringComparer.Ordinal);
            ids.Add(id);
        }
        var edgePairs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var edge in edges.EnumerateArray())
            if (edge.ValueKind == JsonValueKind.Object && GetString(edge, "source") is { } source
                && GetString(edge, "target") is { } target)
                edgePairs.Add(source + "\u001f" + target);

        if (graph.TryGetProperty("connections", out var connections) && connections.ValueKind == JsonValueKind.Array)
            foreach (var connection in connections.EnumerateArray())
            {
                if (connection.ValueKind != JsonValueKind.Object) continue;
                var from = GetString(connection, "from");
                var to = GetString(connection, "to");
                if (from is null || to is null) continue;
                if (!entityByKey.TryGetValue(from, out var fromId) || !entityByKey.TryGetValue(to, out var toId)
                    || !nodeByEntity.TryGetValue(fromId, out var sourceNodes) || !nodeByEntity.TryGetValue(toId, out var targetNodes)
                    || !sourceNodes.Any(src => targetNodes.Any(dst => edgePairs.Contains(src + "\u001f" + dst))))
                    missing.Add(from + " -> " + to);
            }
        return missing;
    }

    private static string? GetString(JsonElement item, string key) =>
        item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    internal static bool MatchesSavedDocument(JsonElement request, JsonElement response, int expectedVersion)
    {
        if (request.ValueKind != JsonValueKind.Object || !request.TryGetProperty("document", out var intended)) return false;
        if (response.ValueKind != JsonValueKind.Object || !response.TryGetProperty("document", out var actual)
            || !response.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number
            || !version.TryGetInt32(out var savedVersion)
            || savedVersion <= expectedVersion) return false;
        return ImportJsonIdentity.Hash(intended) == ImportJsonIdentity.Hash(actual);
    }
}
