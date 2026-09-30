using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using TaskForge.Tasks.Api.Contracts;
using TaskForge.Tasks.Api.Data;
using TaskForge.Tasks.Api.Domain;
using static TaskForge.Tasks.Api.Services.Serialization.AssignmentApiSerializationService;

namespace TaskForge.Tasks.Api.Services.Testing;

internal static class TestAssignmentSpecService
{
    internal static async Task<TaskSpec> ReadAsync(
        TasksDbContext db,
        Assignment assignment,
        CancellationToken ct = default)
    {
        var row = await db.TestAssignmentSpecs
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.AssignmentId == assignment.Id, ct);

        return row is null
            ? ReadLegacy(assignment)
            : ParseTaskSpec(ToNode(row));
    }

    internal static async Task<Dictionary<Guid, TaskSpec>> LoadAsync(
        TasksDbContext db,
        IReadOnlyCollection<Assignment> assignments,
        CancellationToken ct = default)
    {
        var tests = assignments
            .Where(x => string.Equals(x.Type, "test", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (tests.Count == 0) return new Dictionary<Guid, TaskSpec>();

        var ids = tests.Select(x => x.Id).Distinct().ToArray();
        var rows = await db.TestAssignmentSpecs
            .AsNoTracking()
            .Where(x => ids.Contains(x.AssignmentId))
            .ToListAsync(ct);
        var rowsById = rows.ToDictionary(x => x.AssignmentId);
        var result = new Dictionary<Guid, TaskSpec>();

        foreach (var assignment in tests)
        {
            result[assignment.Id] = rowsById.TryGetValue(assignment.Id, out var row)
                ? ParseTaskSpec(ToNode(row))
                : ReadLegacy(assignment);
        }

        return result;
    }

    internal static async Task<TaskSpec> SaveAsync(
        TasksDbContext db,
        Assignment assignment,
        JsonElement payload,
        CancellationToken ct = default)
    {
        var node = JsonNode.Parse(payload.GetRawText()) as JsonObject ?? new JsonObject();
        NormalizeIds(node, "questions");
        return await StoreNodeAsync(db, assignment, node, ct);
    }

    internal static async Task<TaskSpec> MergeFromRequestAsync(
        TasksDbContext db,
        Assignment assignment,
        string? incomingJson,
        CancellationToken ct = default)
    {
        var current = await ReadNodeAsync(db, assignment, ct);
        var mergedJson = MergeInteractiveSpecJsonForStorage(
            current.ToJsonString(JsonOptions()),
            incomingJson,
            "test");
        var node = JsonNode.Parse(string.IsNullOrWhiteSpace(mergedJson) ? "{}" : mergedJson!) as JsonObject ?? new JsonObject();
        NormalizeIds(node, "questions");
        return await StoreNodeAsync(db, assignment, node, ct);
    }

    internal static async Task EnsureDetachedAsync(
        TasksDbContext db,
        Assignment assignment,
        CancellationToken ct = default)
    {
        if (!string.Equals(assignment.Type, "test", StringComparison.OrdinalIgnoreCase)) return;

        var tracked = db.TestAssignmentSpecs.Local.FirstOrDefault(x => x.AssignmentId == assignment.Id);
        var exists = tracked is not null || await db.TestAssignmentSpecs.AnyAsync(x => x.AssignmentId == assignment.Id, ct);
        if (!exists)
        {
            var legacy = ReadLegacyNode(assignment);
            await StoreNodeAsync(db, assignment, legacy, ct);
            return;
        }

        ClearLegacyCodeFields(assignment);
    }

    internal static async Task RemoveAsync(
        TasksDbContext db,
        Guid assignmentId,
        CancellationToken ct = default)
    {
        var tracked = db.TestAssignmentSpecs.Local.FirstOrDefault(x => x.AssignmentId == assignmentId);
        if (tracked is not null)
        {
            db.TestAssignmentSpecs.Remove(tracked);
            return;
        }

        var row = await db.TestAssignmentSpecs.FirstOrDefaultAsync(x => x.AssignmentId == assignmentId, ct);
        if (row is not null) db.TestAssignmentSpecs.Remove(row);
    }

    internal static TaskSpec ReadLegacy(Assignment assignment)
        => ParseTaskSpec(ReadLegacyNode(assignment));

    internal static JsonObject ToNode(TestAssignmentSpec row)
    {
        JsonNode? settings;
        JsonNode? questions;
        try { settings = JsonNode.Parse(string.IsNullOrWhiteSpace(row.SettingsJson) ? "{}" : row.SettingsJson); }
        catch { settings = new JsonObject(); }
        try { questions = JsonNode.Parse(string.IsNullOrWhiteSpace(row.QuestionsJson) ? "[]" : row.QuestionsJson); }
        catch { questions = new JsonArray(); }

        return new JsonObject
        {
            ["settings"] = settings is JsonObject ? settings : new JsonObject(),
            ["questions"] = questions is JsonArray ? questions : new JsonArray()
        };
    }

    private static async Task<JsonObject> ReadNodeAsync(
        TasksDbContext db,
        Assignment assignment,
        CancellationToken ct)
    {
        var tracked = db.TestAssignmentSpecs.Local.FirstOrDefault(x => x.AssignmentId == assignment.Id);
        if (tracked is not null) return ToNode(tracked);

        var row = await db.TestAssignmentSpecs
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.AssignmentId == assignment.Id, ct);
        return row is null ? ReadLegacyNode(assignment) : ToNode(row);
    }

    private static JsonObject ReadLegacyNode(Assignment assignment)
    {
        try
        {
            return JsonNode.Parse(string.IsNullOrWhiteSpace(assignment.TestsJson) ? "{}" : assignment.TestsJson!) as JsonObject ?? new JsonObject();
        }
        catch
        {
            return new JsonObject();
        }
    }

    private static async Task<TaskSpec> StoreNodeAsync(
        TasksDbContext db,
        Assignment assignment,
        JsonObject node,
        CancellationToken ct)
    {
        var parsed = ParseTaskSpec(node);
        var canonical = TaskSpecToJsonObject(parsed);
        var settingsJson = (canonical["settings"] as JsonObject ?? new JsonObject()).ToJsonString(JsonOptions());
        var questionsJson = (canonical["questions"] as JsonArray ?? new JsonArray()).ToJsonString(JsonOptions());

        var row = db.TestAssignmentSpecs.Local.FirstOrDefault(x => x.AssignmentId == assignment.Id)
                  ?? await db.TestAssignmentSpecs.FirstOrDefaultAsync(x => x.AssignmentId == assignment.Id, ct);
        if (row is null)
        {
            row = new TestAssignmentSpec
            {
                AssignmentId = assignment.Id,
                CreatedAt = DateTimeOffset.UtcNow
            };
            db.TestAssignmentSpecs.Add(row);
        }

        row.SettingsJson = settingsJson;
        row.QuestionsJson = questionsJson;
        row.UpdatedAt = DateTimeOffset.UtcNow;

        ClearLegacyCodeFields(assignment);
        assignment.UpdatedAt = DateTimeOffset.UtcNow;
        return parsed;
    }

    private static void ClearLegacyCodeFields(Assignment assignment)
    {
        assignment.TestsJson = null;
        assignment.Language = string.Empty;
        assignment.AllowedLanguagesCsv = null;
        assignment.StarterCode = null;
        assignment.CodeForbiddenCallsJson = null;
        assignment.CodeRequiredCallsJson = null;
    }
}
