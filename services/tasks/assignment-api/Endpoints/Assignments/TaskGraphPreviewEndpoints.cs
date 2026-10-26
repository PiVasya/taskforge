using TaskForge.Shared.Import;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TaskForge.Tasks.Api.Contracts;
using TaskForge.Tasks.Api.Data;
using TaskForge.Tasks.Api.Domain;
using TaskForge.Tasks.Api.Services.Sql;
using static TaskForge.Tasks.Api.Services.Access.AssignmentApiAccessService;
using static TaskForge.Tasks.Api.Services.Common.AssignmentApiCommonService;
using static TaskForge.Tasks.Api.Services.Mapping.AssignmentApiMappingService;
using static TaskForge.Tasks.Api.Services.Serialization.AssignmentApiSerializationService;
using static TaskForge.Tasks.Api.Services.Serialization.AssignmentTaskGraphJsonService;
using static TaskForge.Tasks.Api.Services.Testing.AssignmentApiTestingService;

namespace TaskForge.Tasks.Api.Endpoints;

internal static partial class AssignmentApiEndpoints
{
    private static async Task<List<ValidationIssue>> CheckTaskGraphBindingsAsync(
        ParsedGraph graph, Guid courseId, IReadOnlySet<Guid> subtree, TasksDbContext db,
        IHttpClientFactory clients, IConfiguration cfg, CancellationToken ct)
    {
        var ids = graph.Tasks.Select(task => task.Source.TryGetProperty("id", out var value)
                && value.ValueKind == JsonValueKind.String && Guid.TryParse(value.GetString(), out var id) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty).Distinct().ToArray();
        var assignments = await db.Assignments.AsNoTracking().Where(x => ids.Contains(x.Id))
            .Select(x => new { x.Id, x.CourseId }).ToDictionaryAsync(x => x.Id, x => x.CourseId, ct);
        var requestedCourses = graph.Courses.Select(x => x.Id).Where(x => x != Guid.Empty).Distinct().ToArray();
        var occupied = new HashSet<Guid>();
        if (requestedCourses.Length > 0)
        {
            var probe = await PostInternalAsync<CourseGraphImportProbeResponse>(clients, cfg,
                ServiceUrl(cfg, "EducationApi", "http://education-api:8080"), "/api/internal/courses/import-probe",
                new { courseIds = requestedCourses }, ct);
            if (probe is null) return [new("$.courses", "Не удалось проверить занятость UUID курсов. Импорт заблокирован; повторите проверку.")];
            occupied.UnionWith(probe.ExistingIds);
        }
        return ValidateBindings(graph, courseId, subtree, assignments, occupied);
    }

    private static void MapTaskGraphPreviewEndpoints(WebApplication app)
    {
        app.MapPost("/api/courses/{courseId:guid}/assignments/import-json/preview", PreviewTaskGraphAsync)
            .AddEndpointFilter<SqlEndpointFilter>();
    }

    private static async Task<IResult> PreviewTaskGraphAsync(Guid courseId, JsonElement payload, HttpContext http,
        IConfiguration cfg, TasksDbContext db, IHttpClientFactory clients, CancellationToken ct)
    {
            if (!IsEditor(http, cfg)) return Microsoft.AspNetCore.Http.Results.Json(
                new { code = "EDITOR_REQUIRED", message = "Для проверки импорта нужны права редактора." }, statusCode: 403);
            if (!await CanUserEditCourseAsync(courseId, http, cfg, clients, ct)) return CourseEditForbidden();
            TaskForge.Tasks.Api.Services.Transfer.GraphTransferProgress.Get(http)?.Report("preview.validate", 10);
            var input = ReadGraphImportPayload(payload);
            if (!LooksLikeCanonicalGraph(input)) return Microsoft.AspNetCore.Http.Results.Ok(new { issues = Array.Empty<ValidationIssue>() });
            var options = ReadGraphImportOptions(payload);
            var parsed = ParseAndValidate(input, options);
            if (parsed.Graph is null || parsed.Issues.Count > 0)
                return Microsoft.AspNetCore.Http.Results.Ok(new { issues = parsed.Issues });
            // Preview must use exactly the same stable key-to-UUID reservation as Apply.
            // Otherwise the UI incorrectly labels a repeated keyed batch as "create".
            var reserved = ImportJsonIdentity.ReserveMissingIds(input, courseId);
            var withIds = ParseAndValidate(reserved, options);
            if (withIds.Graph is null || withIds.Issues.Count > 0)
                return Microsoft.AspNetCore.Http.Results.Ok(new { issues = withIds.Issues });
            var graph = withIds.Graph;
            options = options with { UpdateContent = options.UpdateContent && graph.Scopes.Contains("content"),
                UpdateChecks = options.UpdateChecks && graph.Scopes.Contains("checks") };
            Console.WriteLine($"TASK_GRAPH_PREVIEW_BEGIN COURSE={courseId:D} TASKS={graph.Tasks.Count} COURSES={graph.Courses.Count}");
            var tree = await GetInternalAsync<CourseTreeResponse>(clients, cfg,
                ServiceUrl(cfg, "EducationApi", "http://education-api:8080"), $"/api/internal/courses/{courseId:D}/tree", ct);
            if (tree is null || tree.CourseIds.Length == 0)
                return Microsoft.AspNetCore.Http.Results.Json(new { code = "COURSE_TREE_UNAVAILABLE",
                    message = "Не удалось получить поддерево курса для проверки." }, statusCode: 503);
            var subtree = tree.CourseIds.Where(x => x != Guid.Empty).Append(courseId).ToHashSet();
            var user = TaskForgeRequestSecurity.UserId(http, cfg);
            if (!user.HasValue) return Microsoft.AspNetCore.Http.Results.Unauthorized();
            var access = await LoadCourseAccessRowsAsync(subtree, user.Value, clients, cfg, ct);
            var editable = access.Where(x => x.CanEdit).Select(x => x.CourseId).ToHashSet();
            if (subtree.Any(id => !editable.Contains(id))) return CourseEditForbidden();
            TaskForge.Tasks.Api.Services.Transfer.GraphTransferProgress.Get(http)?.Report("preview.bindings", 40, 0, graph.Tasks.Count);
            var issues = await CheckTaskGraphBindingsAsync(graph, courseId, subtree, db, clients, cfg, ct);
            if (issues.Count == 0)
            {
                var ids = graph.Tasks.Select(task => task.Source.TryGetProperty("id", out var value)
                        && value.ValueKind == JsonValueKind.String && Guid.TryParse(value.GetString(), out var id) ? id : Guid.Empty)
                    .Where(id => id != Guid.Empty).Distinct().ToArray();
                var existing = (await db.Assignments.AsNoTracking().Where(x => ids.Contains(x.Id)).Select(x => x.Id).ToListAsync(ct)).ToHashSet();
                for (var index = 0; index < graph.Tasks.Count; index++)
                {
                    ct.ThrowIfCancellationRequested();
                    if (index % 25 == 0) TaskForge.Tasks.Api.Services.Transfer.GraphTransferProgress.Get(http)?.Report("preview.assignments", 45 + 35 * index / Math.Max(1, graph.Tasks.Count), index, graph.Tasks.Count);
                    try
                    {
                        var request = AssignmentRequestFromJson(graph.Tasks[index].Source);
                        var isExisting = request.Id.HasValue && existing.Contains(request.Id.Value);
                        var validation = isExisting ? FilterExistingImportRequest(request, options) : request with { Id = null };
                        foreach (var message in ValidateImportedAssignment(validation, index + 1)) issues.Add(new($"$.tasks[{index}]", message));
                        if (!isExisting && !graph.Scopes.Contains("content"))
                            issues.Add(new($"$.tasks[{index}]", "Для нового задания нужны scope content и полное описание."));
                    }
                    catch (Exception error) when (error is ArgumentException or JsonException or InvalidOperationException)
                    {
                        issues.Add(new($"$.tasks[{index}]", "Не удалось прочитать задание: " + error.Message));
                    }
                }
            }
            TaskForge.Tasks.Api.Services.Transfer.GraphTransferProgress.Get(http)?.Report("preview.resources", 85, graph.Tasks.Count, graph.Tasks.Count);
            var datasets = new List<SqlTaskGraphService.DatasetPreview>();
            if (issues.Count == 0)
            {
                var (sqlUser, sqlAdmin) = SqlTaskService.Editor(http, cfg);
                datasets = await SqlTaskGraphService.PreviewDatasets(db, input, options, sqlUser, sqlAdmin, ct);
            }
            Console.WriteLine($"TASK_GRAPH_PREVIEW_END COURSE={courseId:D} ISSUES={issues.Count} DATASETS={datasets.Count}");
            var bindings = new
            {
                courses = graph.Courses.Select(x => new { key = x.Key, id = x.Id }).ToArray(),
                tasks = graph.Tasks.Select(x => new
                {
                    key = x.Key,
                    id = x.Source.TryGetProperty("id", out var value) && value.ValueKind == JsonValueKind.String
                        ? value.GetString() : null
                }).ToArray()
            };
            var requestedIds = graph.Tasks.Select(x => x.Source.TryGetProperty("id", out var value)
                && value.ValueKind == JsonValueKind.String && Guid.TryParse(value.GetString(), out var id) ? id : Guid.Empty).ToArray();
            var metadata = await db.Assignments.AsNoTracking().Where(x => requestedIds.Contains(x.Id) && subtree.Contains(x.CourseId))
                .Select(x => new { x.Id, x.CourseId, x.Title, x.Type }).ToListAsync(ct);
            return Microsoft.AspNetCore.Http.Results.Ok(new { issues, datasets, bindings, metadata });
    }
}
