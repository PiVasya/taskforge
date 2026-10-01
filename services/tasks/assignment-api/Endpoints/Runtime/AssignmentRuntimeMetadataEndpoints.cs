using Microsoft.EntityFrameworkCore;
using TaskForge.Tasks.Api.Data;
using TaskForge.Tasks.Api.Services.Runtime;
using static TaskForge.Tasks.Api.Services.Access.AssignmentApiAccessService;

namespace TaskForge.Tasks.Api.Endpoints;

internal static partial class AssignmentApiEndpoints
{
    private static WebApplication MapAssignmentRuntimeMetadataEndpoints(WebApplication app)
    {
        app.MapGet("/api/assignments/{assignmentId:guid}/runtime-meta", async (
            Guid assignmentId,
            HttpContext http,
            IConfiguration cfg,
            TasksDbContext db,
            IHttpClientFactory clients,
            CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            var assignment = await db.Assignments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == assignmentId, ct);
            if (assignment is null)
                return Results.NotFound(new { message = "Задание не найдено.", code = "ASSIGNMENT_NOT_FOUND" });

            var canEdit = await CanUserEditCourseAsync(assignment.CourseId, http, cfg, clients, ct);
            if (!canEdit && !await CanUserAccessAssignmentAsync(assignment, http, cfg, db, clients, ct))
                return Results.NotFound(new { message = "Задание не найдено.", code = "ASSIGNMENT_NOT_FOUND" });

            var type = AssignmentTypedReadService.NormalizeStoredType(assignment);
            var route = type switch
            {
                "code-test" => "code-assignments",
                "image-test" => "image-assignments",
                "test" => "test-assignments",
                "math" => "math-assignments",
                "sql-test" => "sql-assignments",
                _ => throw new InvalidOperationException($"Unsupported assignment type '{type}'.")
            };
            return Results.Ok(new { assignmentId = assignment.Id, assignment.CourseId, type, route, canEdit });
        });

        return app;
    }
}
