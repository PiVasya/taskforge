using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using TaskForge.Tasks.Api.Data;
using TaskForge.Tasks.Api.Services.Sql;
using static TaskForge.Tasks.Api.Services.Common.AssignmentApiCommonService;
using TaskForge.Tasks.Api.Services.Serialization;
using static TaskForge.Tasks.Api.Services.Serialization.AssignmentTaskGraphJsonService;

namespace TaskForge.Tasks.Api.Endpoints;

internal static partial class AssignmentApiEndpoints
{
    private const string ImportOperationContextKey = "TaskGraphImport.OperationId";

    internal static bool IsLayoutOnlyGraphImport(ParsedGraph? graph, GraphImportOptions options)
    {
        if (graph is null || graph.SourceSchemaVersion != SchemaVersion || !graph.Layout.HasValue
            || !graph.Scopes.Contains("layout") || !options.UpdateLayout || graph.DeleteTaskIds.Count != 0)
            return false;

        return !(options.UpdateContent && graph.Scopes.Contains("content"))
            && !(options.UpdateChecks && graph.Scopes.Contains("checks"))
            && !(options.UpdateVisibility && graph.Scopes.Contains("visibility"))
            && !(options.UpdateConnections && graph.Scopes.Contains("connections"))
            && !(options.UpdateConnectionAccess && graph.Scopes.Contains("connectionAccess"));
    }

    internal static bool IsMetadataOnlyGraphImport(ParsedGraph? graph, GraphImportOptions options)
        => graph is not null && graph.SourceSchemaVersion == SchemaVersion && graph.DeleteTaskIds.Count == 0
            && !(options.UpdateContent && graph.Scopes.Contains("content"))
            && !(options.UpdateChecks && graph.Scopes.Contains("checks"));

    private static bool IsLayoutOnlyGraphImport(JsonElement payload)
    {
        var graph = ReadGraphImportPayload(payload);
        if (!LooksLikeCanonicalGraph(graph)) return false;
        var options = ReadGraphImportOptions(payload);
        var parsed = ParseAndValidate(graph, options);
        return parsed.Issues.Count == 0 && IsLayoutOnlyGraphImport(parsed.Graph, options);
    }

    private static void LogImportPhase(HttpContext http, Stopwatch elapsed, string phase, int tasks = 0, int courses = 0)
    {
        TaskForge.Tasks.Api.Services.Transfer.GraphTransferProgress.Get(http)?.ImportPhase(phase, tasks, courses);
        var operation = http.Items.TryGetValue(ImportOperationContextKey, out var value) && value is Guid id
            ? id.ToString("D") : "none";
        Console.WriteLine($"TASK_GRAPH_IMPORT_PHASE OPERATION={operation} TRACE={http.TraceIdentifier} " +
            $"PHASE={phase} ELAPSED_MS={elapsed.ElapsedMilliseconds} TASKS={tasks} COURSES={courses} " +
            $"REQUEST_ABORTED={http.RequestAborted.IsCancellationRequested}");
    }

    private static async Task<IResult> ApplyMetadataOnlyGraphImportAsync(ParsedGraph graph,
        GraphImportOptions options, IReadOnlyDictionary<string, Guid> courseIds, IReadOnlySet<Guid> subtree, TasksDbContext db,
        IHttpClientFactory clients, IConfiguration cfg, HttpContext http, Stopwatch elapsed, CancellationToken ct)
    {
        LogImportPhase(http, elapsed, "layout-only.validate-ids", graph.Tasks.Count, graph.Courses.Count);
        var issues = new List<object>();
        for (var i = 0; i < graph.Courses.Count; i++)
        {
            var course = graph.Courses[i];
            if (course.Id == Guid.Empty || !subtree.Contains(course.Id))
                issues.Add(new { path = $"$.courses[{i}].id",
                    message = "A layout-only import requires an existing nested course UUID in the current subtree." });
        }

        var taskIds = new HashSet<Guid>();
        for (var i = 0; i < graph.Tasks.Count; i++)
        {
            var source = graph.Tasks[i].Source;
            if (source.TryGetProperty("id", out var value) && value.ValueKind == JsonValueKind.String
                && Guid.TryParse(value.GetString(), out var id) && id != Guid.Empty)
                taskIds.Add(id);
            else
                issues.Add(new { path = $"$.tasks[{i}].id",
                    message = "A layout-only import requires a valid existing assignment UUID." });
        }
        if (issues.Count != 0)
            return Microsoft.AspNetCore.Http.Results.BadRequest(new { code = "TASK_GRAPH_LAYOUT_ID_INVALID", issues });

        var existing = await db.Assignments.AsNoTracking()
            .Where(x => taskIds.Contains(x.Id))
            .Select(x => new { x.Id, x.CourseId, x.Title, x.Type, x.Rating, x.IsVisible })
            .ToListAsync(ct);
        LogImportPhase(http, elapsed, "layout-only.assignments-loaded", existing.Count, graph.Courses.Count);
        var byId = existing.ToDictionary(x => x.Id);
        for (var i = 0; i < graph.Tasks.Count; i++)
        {
            var task = graph.Tasks[i];
            var id = Guid.Parse(task.Source.GetProperty("id").GetString()!);
            if (!byId.TryGetValue(id, out var row) || !subtree.Contains(row.CourseId))
            {
                issues.Add(new { path = $"$.tasks[{i}].id",
                    message = "A layout-only import cannot create assignments or refer to assignments outside the subtree." });
                continue;
            }
            if (task.Source.TryGetProperty("course", out _)
                && (!courseIds.TryGetValue(task.CourseRef, out var expectedCourseId) || row.CourseId != expectedCourseId))
                issues.Add(new { path = $"$.tasks[{i}].course",
                    message = "The existing assignment belongs to a different course. Layout imports cannot move assignments." });
        }
        if (issues.Count != 0)
            return Microsoft.AspNetCore.Http.Results.BadRequest(new { code = "TASK_GRAPH_LAYOUT_ID_INVALID", issues });

        var visibility = new Dictionary<Guid, bool>();
        var changedVisibility = new HashSet<Guid>();
        if (options.UpdateVisibility && graph.Scopes.Contains("visibility"))
        {
            foreach (var task in graph.Tasks)
                if (task.Source.TryGetProperty("isVisible", out var visible) && visible.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    visibility[Guid.Parse(task.Source.GetProperty("id").GetString()!)] = visible.GetBoolean();
            foreach (var item in visibility.Where(x => x.Value && byId[x.Key].Type == "sql-test"))
                if (!await SqlTaskService.HasPublishedRevision(db, item.Key, ct))
                    return Microsoft.AspNetCore.Http.Results.Conflict(new { code = "SQL_NOT_PUBLISHED", assignmentId = item.Key });
            foreach (var value in new[] { false, true })
            {
                var changed = visibility.Where(x => x.Value == value && byId[x.Key].IsVisible != value).Select(x => x.Key).ToArray();
                changedVisibility.UnionWith(changed);
                if (changed.Length > 0) await db.Assignments.Where(x => changed.Contains(x.Id))
                    .ExecuteUpdateAsync(set => set.SetProperty(x => x.IsVisible, value).SetProperty(x => x.UpdatedAt, DateTimeOffset.UtcNow), ct);
            }
        }
        foreach (var id in changedVisibility)
        {
            var users = await db.Attempts.AsNoTracking().Where(x => x.TaskAssignmentId == id).Select(x => x.UserId).Distinct().ToListAsync(ct);
            await MarkAssignmentRatingDirtyInSolutionsAsync(clients, cfg, id, users, "assignment-import-updated", ct);
        }
        var effectiveCourses = courseIds.ToDictionary(x => x.Value, x => x.Key);
        var mappedCourses = new JsonArray();
        foreach (var course in graph.Courses)
            mappedCourses.Add(new JsonObject
            {
                ["key"] = course.Key,
                ["id"] = courseIds[course.Key].ToString("D"),
                ["title"] = course.Title
            });

        var mappedTasks = new JsonArray();
        var assignmentDtos = new JsonArray();
        foreach (var task in graph.Tasks)
        {
            var id = Guid.Parse(task.Source.GetProperty("id").GetString()!);
            var assignment = byId[id];
            var taskReceipt = new JsonObject
            {
                ["key"] = task.Key,
                ["assignmentId"] = id.ToString("D"),
                ["type"] = assignment.Type,
                ["action"] = changedVisibility.Contains(id) ? "updated" : "unchanged"
            };
            if (effectiveCourses.TryGetValue(assignment.CourseId, out var courseRef)) taskReceipt["course"] = courseRef;
            mappedTasks.Add(taskReceipt);
            // The map client needs IDs and types; layout imports never return or mutate checks/content.
            assignmentDtos.Add(new JsonObject
            {
                ["id"] = id,
                ["courseId"] = assignment.CourseId,
                ["type"] = assignment.Type,
                ["title"] = assignment.Title,
                ["rating"] = assignment.Rating,
                ["isVisible"] = visibility.GetValueOrDefault(id, assignment.IsVisible)
            });
        }

        var receipt = new JsonObject
        {
            ["schemaVersion"] = SchemaVersion,
            ["format"] = Format,
            ["scopes"] = BuildScopesJson(graph.Scopes),
            ["courses"] = mappedCourses,
            ["tasks"] = mappedTasks,
            ["deleteTasks"] = new JsonArray(),
            ["datasets"] = new JsonArray(),
            ["connections"] = new JsonArray(graph.Connections.Select(x => (JsonNode)x.ToJson()).ToArray()),
            ["layout"] = graph.Layout.HasValue ? JsonNode.Parse(graph.Layout.Value.GetRawText()) : null,
            ["apply"] = new JsonObject
            {
                ["content"] = false,
                ["checks"] = false,
                ["visibility"] = options.UpdateVisibility && graph.Scopes.Contains("visibility"),
                ["connections"] = options.UpdateConnections && graph.Scopes.Contains("connections"),
                ["connectionMode"] = options.ConnectionMode,
                ["connectionAccess"] = options.UpdateConnectionAccess && graph.Scopes.Contains("connectionAccess"),
                ["layout"] = options.UpdateLayout && graph.Scopes.Contains("layout") && graph.Layout.HasValue,
                ["deletions"] = false
            }
        };
        LogImportPhase(http, elapsed, "layout-only.receipt-ready", graph.Tasks.Count, graph.Courses.Count);
        return Microsoft.AspNetCore.Http.Results.Ok(new
        {
            createdCount = 0,
            updatedCount = changedVisibility.Count,
            deletedCount = 0,
            alreadyDeletedCount = 0,
            deletedAssignmentIds = Array.Empty<Guid>(),
            createdCourseCount = 0,
            totalCount = graph.Tasks.Count,
            assignments = assignmentDtos,
            taskGraph = receipt
        });
    }

    internal static void AddLayoutOnlyEntityAudit(JsonObject response)
    {
        var entities = new JsonArray();
        foreach (var task in response["taskGraph"]?["tasks"]?.AsArray() ?? new JsonArray())
        {
            if (task?["assignmentId"] is null) continue;
            entities.Add(new JsonObject
            {
                ["assignmentId"] = task["assignmentId"]!.DeepClone(),
                ["action"] = "unchanged",
                ["changedFields"] = new JsonArray()
            });
        }
        response["entityChanges"] = entities;
        response["updatedCount"] = 0;
        response["unchangedCount"] = entities.Count;
    }
}
