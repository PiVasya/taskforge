using TaskForge.Tasks.Api.Contracts;
using TaskForge.Tasks.Api.Data;
using TaskForge.Tasks.Api.Services.Authoring;
using TaskForge.Tasks.Api.Services.Sql;
using static TaskForge.Tasks.Api.Services.Access.AssignmentApiAccessService;
using static TaskForge.Tasks.Api.Services.Results.AssignmentApiResultsService;
using static TaskForge.Tasks.Api.Services.Serialization.AssignmentApiSerializationService;

namespace TaskForge.Tasks.Api.Endpoints;

internal static partial class AssignmentApiEndpoints
{
    private static WebApplication MapSqlAssignmentAuthoringEndpoints(WebApplication app)
    {
        app.MapPost("/api/courses/{courseId:guid}/sql-assignments", async (
            Guid courseId,
            SqlAssignmentAuthoringRequest request,
            HttpContext http,
            IConfiguration cfg,
            TasksDbContext db,
            IHttpClientFactory clients,
            CancellationToken ct) =>
        {
            if (!await CanUserEditCourseAsync(courseId, http, cfg, clients, ct)) return CourseEditForbidden();
            var assignment = AssignmentAuthoringCommonService.BuildBase(courseId, "sql-test", request, await NextAssignmentSortAsync(db, courseId, ct));
            assignment.IsVisible = false;
            db.Assignments.Add(assignment);
            await db.SaveChangesAsync(ct);
            return Results.Json(AssignmentAuthoringCommonService.BuildEditMetaDto(assignment), JsonOptions());
        });

        app.MapGet("/api/sql-assignments/{assignmentId:guid}/edit", async (
            Guid assignmentId,
            HttpContext http,
            IConfiguration cfg,
            TasksDbContext db,
            IHttpClientFactory clients,
            CancellationToken ct) =>
        {
            var loaded = await RequireTypedAssignmentForEditAsync(assignmentId, "sql-test", http, cfg, db, clients, ct);
            if (loaded.Error is not null) return loaded.Error;
            return Results.Json(AssignmentAuthoringCommonService.BuildEditMetaDto(loaded.Assignment!), JsonOptions());
        });

        app.MapPut("/api/sql-assignments/{assignmentId:guid}", async (
            Guid assignmentId,
            SqlAssignmentAuthoringRequest request,
            HttpContext http,
            IConfiguration cfg,
            TasksDbContext db,
            IHttpClientFactory clients,
            CancellationToken ct) =>
        {
            var loaded = await RequireTypedAssignmentForEditAsync(assignmentId, "sql-test", http, cfg, db, clients, ct, tracked: true);
            if (loaded.Error is not null) return loaded.Error;
            var assignment = loaded.Assignment!;
            var requestedVisible = request.IsHidden.HasValue ? !request.IsHidden.Value : request.IsVisible;
            if (requestedVisible == true && !await SqlTaskService.HasPublishedRevision(db, assignmentId, ct))
                return Results.Conflict(new { code = "SQL_NOT_PUBLISHED", message = "SQL-задание нужно сначала опубликовать." });

            var oldRating = assignment.Rating;
            var oldVisible = assignment.IsVisible;
            AssignmentAuthoringCommonService.ApplyMetadata(assignment, request);
            await db.SaveChangesAsync(ct);
            await MarkTypedAssignmentChangeAsync(assignment, oldRating, oldVisible, clients, cfg, db, ct);
            return Results.Json(AssignmentAuthoringCommonService.BuildEditMetaDto(assignment), JsonOptions());
        });

        return app;
    }
}
