using Microsoft.EntityFrameworkCore;
using TaskForge.Tasks.Api.Data;
using TaskForge.Tasks.Api.Services.Authoring;
using static TaskForge.Tasks.Api.Services.Access.AssignmentApiAccessService;
using static TaskForge.Tasks.Api.Services.Results.AssignmentApiResultsService;
using static TaskForge.Tasks.Api.Services.Serialization.AssignmentApiSerializationService;

namespace TaskForge.Tasks.Api.Endpoints;

internal static partial class AssignmentApiEndpoints
{
    private static WebApplication MapAssignmentAuthoringMetadataEndpoints(WebApplication app)
    {
        app.MapGet("/api/assignments/{assignmentId:guid}/edit-meta", async (
            Guid assignmentId,
            HttpContext http,
            IConfiguration cfg,
            TasksDbContext db,
            IHttpClientFactory clients,
            CancellationToken ct) =>
        {
            if (!IsEditor(http, cfg))
                return Results.Json(new { message = "Для редактирования нужны права редактора.", code = "EDITOR_REQUIRED" }, statusCode: StatusCodes.Status403Forbidden);
            var assignment = await db.Assignments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == assignmentId, ct);
            if (assignment is null)
                return Results.NotFound(new { message = "Задание не найдено.", code = "ASSIGNMENT_NOT_FOUND" });
            if (!await CanUserEditCourseAsync(assignment.CourseId, http, cfg, clients, ct)) return CourseEditForbidden();
            return Results.Json(AssignmentAuthoringCommonService.BuildEditMetaDto(assignment), JsonOptions());
        });

        return app;
    }
}
