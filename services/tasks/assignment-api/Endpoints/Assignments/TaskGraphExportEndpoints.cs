using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using TaskForge.Tasks.Api.Contracts;
using TaskForge.Tasks.Api.Data;
using TaskForge.Tasks.Api.Domain;
using TaskForge.Tasks.Api.Services.Serialization;
using TaskForge.Tasks.Api.Services.Sql;
using TaskForge.Tasks.Api.Services.Transfer;
using static TaskForge.Tasks.Api.Services.Access.AssignmentApiAccessService;
using static TaskForge.Tasks.Api.Services.Common.AssignmentApiCommonService;
using static TaskForge.Tasks.Api.Services.Mapping.AssignmentApiMappingService;
using static TaskForge.Tasks.Api.Services.Serialization.AssignmentTaskGraphJsonService;

namespace TaskForge.Tasks.Api.Endpoints;

internal static partial class AssignmentApiEndpoints
{
    private static Task<IResult> ExportTaskGraphHttpAsync(Guid courseId, bool? includeIds, bool? includeContent,
        bool? includeChecks, bool? includeVisibility, bool? includeConnections, bool? includeConnectionAccess,
        bool? includeLayout, bool? includeGuide, HttpContext http, IConfiguration cfg, TasksDbContext db,
        IHttpClientFactory clients, CancellationToken ct) => ExportTaskGraphCoreAsync(courseId,
            new(includeIds ?? true, includeContent ?? true, includeChecks ?? true, includeVisibility ?? true,
                includeConnections ?? true, includeConnectionAccess ?? true, includeLayout ?? true, includeGuide ?? false),
            http, cfg, db, clients, ct);

    internal static GraphExportOptions ReadTransferExportOptions(JsonElement options) => new(
        ReadOption(options, "includeIds", true), ReadOption(options, "includeContent", true),
        ReadOption(options, "includeChecks", true), ReadOption(options, "includeVisibility", true),
        ReadOption(options, "includeConnections", true), ReadOption(options, "includeConnectionAccess", true),
        ReadOption(options, "includeLayout", true), ReadOption(options, "includeGuide", false));

    internal static IQueryable<Assignment> ExportAssignmentQuery(TasksDbContext db, Guid[] courseIds, GraphExportOptions options)
    {
        // Legacy executable payload columns are never part of this projection.
        var source = db.Assignments.AsNoTracking().Where(x => courseIds.Contains(x.CourseId));
        var rows = options.IncludeContent
            ? source.Select(x => new Assignment { Id = x.Id, CourseId = x.CourseId, Title = x.Title, Type = x.Type,
                Description = x.Description, Tags = x.Tags, Rating = x.Rating, IsVisible = x.IsVisible,
                Sort = x.Sort, CreatedAt = x.CreatedAt })
            : source.Select(x => new Assignment { Id = x.Id, CourseId = x.CourseId, Title = x.Title, Type = x.Type,
                Rating = x.Rating, IsVisible = x.IsVisible, Sort = x.Sort, CreatedAt = x.CreatedAt });
        return rows.OrderBy(x => x.CourseId).ThenBy(x => x.Sort).ThenBy(x => x.CreatedAt).ThenBy(x => x.Id);
    }

    private static async Task<IResult> ExportTaskGraphCoreAsync(Guid courseId, GraphExportOptions options,
        HttpContext http, IConfiguration cfg, TasksDbContext db, IHttpClientFactory clients, CancellationToken ct)
    {
        if (!IsEditor(http, cfg)) return Microsoft.AspNetCore.Http.Results.StatusCode(403);
        if (!options.IncludeIds && !options.IncludeContent)
            return Microsoft.AspNetCore.Http.Results.BadRequest(new { code = "TASK_GRAPH_EXPORT_INCOMPLETE" });
        var progress = GraphTransferProgress.Get(http);
        progress?.Report("export.tree", 5);
        var url = ServiceUrl(cfg, "EducationApi", "http://education-api:8080");
        var tree = await GetInternalAsync<CourseTreeResponse>(clients, cfg, url, $"/api/internal/courses/{courseId:D}/tree", ct);
        if (tree is null || tree.CourseIds.Length == 0)
            return Microsoft.AspNetCore.Http.Results.Json(new { code = "COURSE_TREE_UNAVAILABLE" }, statusCode: 503);
        var ids = tree.CourseIds.Append(courseId).Distinct().ToArray();
        var user = TaskForgeRequestSecurity.UserId(http, cfg);
        if (!user.HasValue) return Microsoft.AspNetCore.Http.Results.Unauthorized();
        var access = await LoadCourseAccessRowsAsync(ids, user.Value, clients, cfg, ct);
        var editable = access.Where(x => x.CanEdit).Select(x => x.CourseId).ToHashSet();
        if (ids.Any(id => !editable.Contains(id))) return CourseEditForbidden();
        progress?.Report("export.metadata", 10);
        var rows = await ExportAssignmentQuery(db, ids, options).Take(MaxTasks + 1).ToListAsync(ct);
        if (rows.Count > MaxTasks) return Microsoft.AspNetCore.Http.Results.BadRequest(new { code = "TASK_GRAPH_TOO_MANY_TASKS", maximum = MaxTasks });
        var map = await GetInternalAsync<CourseMapInternalResponse>(clients, cfg, url, $"/api/internal/courses/{courseId:D}/map", ct);
        if (map is null) return Microsoft.AspNetCore.Http.Results.Json(new { code = "COURSE_MAP_UNAVAILABLE" }, statusCode: 503);
        var dtos = new Dictionary<Guid, JsonObject>();
        var completed = 0;
        foreach (var batch in rows.Chunk(100))
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(options.IncludeContent || options.IncludeChecks ? "export.specs" : "export.references",
                20 + 50 * completed / Math.Max(1, rows.Count), completed, rows.Count);
            var loaded = await AssignmentTypedExportService.LoadImportDtosAsync(db, batch, ct, options);
            foreach (var (id, dto) in loaded) dtos[id] = dto;
            completed += batch.Length;
        }
        progress?.Report("export.resources", 75, completed, rows.Count);
        var sql = options.IncludeContent || options.IncludeChecks
            ? await SqlTaskGraphService.Export(db, rows, options, ct) : null;
        progress?.Report("export.compose", 85, completed, rows.Count);
        var graph = BuildExport(courseId, rows, tree, map, options, sql, dtos);
        return Microsoft.AspNetCore.Http.Results.Json(graph);
    }
}
