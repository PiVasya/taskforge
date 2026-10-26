using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using TaskForge.Shared.Import;
using TaskForge.Tasks.Api.Data;
using TaskForge.Tasks.Api.Services.Runtime;
using TaskForge.Tasks.Api.Domain;
using TaskForge.Tasks.Api.Services.Serialization;
using TaskForge.Tasks.Api.Services.Sql;
using static TaskForge.Tasks.Api.Services.Serialization.AssignmentTaskGraphJsonService;
using static TaskForge.Tasks.Api.Services.Serialization.AssignmentApiSerializationService;

namespace TaskForge.Tasks.Api.Endpoints;

internal static partial class AssignmentApiEndpoints
{
    internal static JsonObject SafeImportOptions(JsonElement options)
    {
        var result = new JsonObject();
        if (options.ValueKind != JsonValueKind.Object) return result;
        foreach (var name in new[] { "updateContent", "updateChecks", "updateVisibility", "updateConnections",
            "updateConnectionAccess", "updateLayout", "allowDeletes", "allowConnectionDeletes", "allowDuplicates" })
            if (options.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                result[name] = value.GetBoolean();
        if (options.TryGetProperty("connectionMode", out var mode) && mode.ValueKind == JsonValueKind.String
            && mode.GetString() is "merge" or "replace") result["connectionMode"] = mode.GetString();
        if (options.TryGetProperty("expectedMapVersion", out var version) && version.ValueKind == JsonValueKind.Number
            && version.TryGetInt32(out var number) && number >= 0) result["expectedMapVersion"] = number;
        return result;
    }

    internal static string ImportSnapshotHash(IReadOnlyDictionary<Guid, JsonObject> snapshots)
    {
        var document = new JsonObject();
        foreach (var (id, value) in snapshots) document[id.ToString("D")] = ImportJsonIdentity.Hash(JsonSerializer.SerializeToElement(value));
        return ImportJsonIdentity.Hash(JsonSerializer.SerializeToElement(document));
    }

    internal static GraphExportOptions ImportSnapshotOptions(JsonElement payload)
    {
        var input = ReadGraphImportPayload(payload);
        var options = ReadGraphImportOptions(payload);
        var scopes = input.ValueKind == JsonValueKind.Object && input.TryGetProperty("scopes", out var values) && values.ValueKind == JsonValueKind.Array
            ? values.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()).ToHashSet()
            : KnownScopes.Select(x => (string?)x).ToHashSet();
        return new GraphExportOptions(IncludeContent: options.UpdateContent && scopes.Contains("content"),
            IncludeChecks: options.UpdateChecks && scopes.Contains("checks"), IncludeVisibility: options.UpdateVisibility && scopes.Contains("visibility"),
            IncludeConnections: false, IncludeConnectionAccess: false, IncludeLayout: false, IncludeGuide: false);
    }

    private static async Task<Dictionary<Guid, JsonObject>> CaptureImportAssignments(TasksDbContext db, JsonElement graph, CancellationToken ct, GraphExportOptions? options = null)
    {
        var ids = ExtractAssignmentImportItems(graph).Where(x => x.ValueKind == JsonValueKind.Object)
            .Select(x => x.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && Guid.TryParse(id.GetString(), out var value) ? value : Guid.Empty)
            .Where(x => x != Guid.Empty).ToHashSet();
        if (graph.ValueKind == JsonValueKind.Object && graph.TryGetProperty("deleteTasks", out var deletes) && deletes.ValueKind == JsonValueKind.Array)
            foreach (var value in deletes.EnumerateArray())
                if (value.ValueKind == JsonValueKind.String && Guid.TryParse(value.GetString(), out var id)) ids.Add(id);
        return await SnapshotImportAssignments(db, ids, ct, options);
    }

    private static async Task<Dictionary<Guid, JsonObject>> SnapshotImportAssignments(TasksDbContext db, IReadOnlySet<Guid> ids,
        CancellationToken ct, GraphExportOptions? options = null)
    {
        if (options is null) return await SnapshotLegacyImportAssignments(db, ids, ct);
        var source = db.Assignments.AsNoTracking().Where(x => ids.Contains(x.Id));
        var query = options.IncludeContent
            ? source.Select(x => new Assignment { Id = x.Id, CourseId = x.CourseId, Title = x.Title, Type = x.Type,
                Description = x.Description, Tags = x.Tags, Rating = x.Rating, IsVisible = x.IsVisible })
            : source.Select(x => new Assignment { Id = x.Id, CourseId = x.CourseId, Title = x.Title, Type = x.Type,
                Rating = x.Rating, IsVisible = x.IsVisible });
        var rows = await query.ToListAsync(ct);
        var typed = await AssignmentTypedExportService.LoadImportDtosAsync(db, rows, ct, options);
        var sql = await SqlTaskGraphService.Export(db, rows, options, ct);
        var result = new Dictionary<Guid, JsonObject>();
        foreach (var (id, node) in typed)
        {
            var copy = node.DeepClone().AsObject();
            if (!options.IncludeVisibility) copy.Remove("isVisible");
            if (!options.IncludeContent)
                foreach (var field in new[] { "type", "title", "description", "tags", "rating", "language", "allowedLanguages", "starterCode" }) copy.Remove(field);
            if (sql.Specs.TryGetValue(id, out var spec)) copy["sql"] = spec.DeepClone();
            result[id] = copy;
        }
        return result;
    }

    // Schema-1 journals were hashed with the old DTO shape; do not weaken their conflict checks.
    private static async Task<Dictionary<Guid, JsonObject>> SnapshotLegacyImportAssignments(TasksDbContext db, IReadOnlySet<Guid> ids, CancellationToken ct)
    {
        var rows = await db.Assignments.AsNoTracking().Where(x => ids.Contains(x.Id)).ToListAsync(ct);
        var typed = await AssignmentTypedReadService.BuildDtosAsync(db, rows, includeSensitive: true, ct: ct);
        var sql = await SqlTaskGraphService.Export(db, rows, new GraphExportOptions(), ct);
        var result = new Dictionary<Guid, JsonObject>();
        foreach (var node in typed)
        {
            var id = node["id"]!.GetValue<Guid>();
            var copy = node.DeepClone().AsObject();
            foreach (var field in new[] { "createdAt", "updatedAt", "canEdit", "isSolved", "solved" }) copy.Remove(field);
            if (sql.Specs.TryGetValue(id, out var spec)) copy["sql"] = spec.DeepClone();
            result[id] = copy;
        }
        return result;
    }

    internal static JsonObject ImportEntityChange(Guid id, JsonObject? before, JsonObject? after)
    {
        var oldHash = before is null ? null : ImportJsonIdentity.Hash(JsonSerializer.SerializeToElement(before));
        var newHash = after is null ? null : ImportJsonIdentity.Hash(JsonSerializer.SerializeToElement(after));
        var keys = (before?.Select(x => x.Key) ?? []).Concat(after?.Select(x => x.Key) ?? []).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
        var fields = new JsonArray();
        foreach (var key in keys)
        {
            var oldField = before is not null && before.TryGetPropertyValue(key, out var oldValue) ? oldValue : null;
            var newField = after is not null && after.TryGetPropertyValue(key, out var newValue) ? newValue : null;
            if (!JsonNode.DeepEquals(oldField, newField)) fields.Add(key);
        }
        return new JsonObject { ["assignmentId"] = id.ToString("D"),
            ["action"] = before is null ? "created" : after is null ? "deleted" : oldHash == newHash ? "unchanged" : "updated",
            ["beforeSha256"] = oldHash, ["afterSha256"] = newHash, ["changedFields"] = fields };
    }

    internal static JsonObject MapImportAuditState(JsonElement? document, int version)
    {
        var nodes = new JsonArray(); var edges = new JsonArray();
        if (document is { ValueKind: JsonValueKind.Object } doc)
        {
            if (doc.TryGetProperty("nodes", out var sourceNodes) && sourceNodes.ValueKind == JsonValueKind.Array)
                foreach (var item in sourceNodes.EnumerateArray()) nodes.Add(MapAuditFields(item, new[] { "id", "entityId", "type", "position" }));
            if (doc.TryGetProperty("edges", out var sourceEdges) && sourceEdges.ValueKind == JsonValueKind.Array)
                foreach (var item in sourceEdges.EnumerateArray())
                {
                    var row = MapAuditFields(item, new[] { "id", "source", "target" });
                    // Hash settings instead of exporting arbitrary extension properties.
                    if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("settings", out var settings)) row["settingsSha256"] = ImportJsonIdentity.Hash(settings);
                    edges.Add(row);
                }
        }
        return new JsonObject { ["version"] = version, ["nodes"] = nodes, ["edges"] = edges };
    }

    private static JsonObject MapAuditFields(JsonElement source, string[] fields)
    {
        var row = new JsonObject();
        if (source.ValueKind != JsonValueKind.Object) return row;
        foreach (var name in fields)
            if (source.TryGetProperty(name, out var value))
            {
                if (name == "position" && value.ValueKind == JsonValueKind.Object)
                {
                    var position = new JsonObject();
                    foreach (var axis in new[] { "x", "y" })
                        if (value.TryGetProperty(axis, out var coordinate) && coordinate.ValueKind == JsonValueKind.Number) position[axis] = JsonNode.Parse(coordinate.GetRawText());
                    row[name] = position;
                }
                else if (value.ValueKind == JsonValueKind.String) row[name] = value.GetString();
            }
        return row;
    }

    internal static JsonObject MapImportAuditDiff(JsonObject before, JsonObject after)
    {
        var diff = new JsonObject { ["previousVersion"] = before["version"]?.DeepClone(), ["savedVersion"] = after["version"]?.DeepClone() };
        foreach (var kind in new[] { "nodes", "edges" })
        {
            static Dictionary<string, JsonNode?> Index(JsonArray? rows) => (rows ?? new JsonArray())
                .Where(x => !string.IsNullOrWhiteSpace(x?["id"]?.ToString())).GroupBy(x => x!["id"]!.ToString())
                .ToDictionary(x => x.Key, x => x.First(), StringComparer.Ordinal);
            var oldRows = Index(before[kind] as JsonArray); var newRows = Index(after[kind] as JsonArray);
            var added = new JsonArray(); var removed = new JsonArray(); var changed = new JsonArray();
            foreach (var id in newRows.Keys.Except(oldRows.Keys).Order(StringComparer.Ordinal)) added.Add(id);
            foreach (var id in oldRows.Keys.Except(newRows.Keys).Order(StringComparer.Ordinal)) removed.Add(id);
            foreach (var id in newRows.Keys.Intersect(oldRows.Keys).Order(StringComparer.Ordinal))
                if (!JsonNode.DeepEquals(oldRows[id], newRows[id])) changed.Add(id);
            diff[kind] = new JsonObject { ["added"] = added, ["removed"] = removed, ["changed"] = changed };
        }
        return diff;
    }

    private static async Task AddImportEntityAudit(TasksDbContext db, Dictionary<Guid, JsonObject> before, JsonObject response, CancellationToken ct, GraphExportOptions? options = null)
    {
        var ids = before.Keys.ToHashSet();
        foreach (var row in response["assignments"]?.AsArray() ?? new JsonArray())
            if (Guid.TryParse(row?["id"]?.ToString(), out var id)) ids.Add(id);
        var after = await SnapshotImportAssignments(db, ids, ct, options);
        var changes = ids.Order().Select(id => ImportEntityChange(id, before.GetValueOrDefault(id), after.GetValueOrDefault(id))).ToArray();
        response["entityChanges"] = new JsonArray(changes.Select(x => (JsonNode)x).ToArray());
        response["updatedCount"] = changes.Count(x => x["action"]?.ToString() == "updated");
        response["unchangedCount"] = changes.Count(x => x["action"]?.ToString() == "unchanged");
        foreach (var row in response["taskGraph"]?["tasks"]?.AsArray() ?? new JsonArray())
        {
            var change = changes.FirstOrDefault(x => x["assignmentId"]?.ToString() == row?["assignmentId"]?.ToString());
            if (row is JsonObject task && change is not null) task["action"] = change["action"]!.DeepClone();
        }
    }
}
