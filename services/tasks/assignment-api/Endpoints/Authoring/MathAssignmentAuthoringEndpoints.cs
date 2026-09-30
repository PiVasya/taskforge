using TaskForge.Tasks.Api.Contracts;
using TaskForge.Tasks.Api.Data;
using TaskForge.Tasks.Api.Services.Authoring;
using static TaskForge.Tasks.Api.Services.Access.AssignmentApiAccessService;
using static TaskForge.Tasks.Api.Services.Results.AssignmentApiResultsService;
using static TaskForge.Tasks.Api.Services.Serialization.AssignmentApiSerializationService;

namespace TaskForge.Tasks.Api.Endpoints;

internal static partial class AssignmentApiEndpoints
{
    private static WebApplication MapMathAssignmentAuthoringEndpoints(WebApplication app)
    {
        app.MapPost("/api/courses/{courseId:guid}/math-assignments", async (
            Guid courseId,
            MathAssignmentAuthoringRequest request,
            HttpContext http,
            IConfiguration cfg,
            TasksDbContext db,
            IHttpClientFactory clients,
            CancellationToken ct) =>
        {
            if (!await CanUserEditCourseAsync(courseId, http, cfg, clients, ct)) return CourseEditForbidden();
            var assignment = AssignmentAuthoringCommonService.BuildBase(courseId, "math", request, await NextAssignmentSortAsync(db, courseId, ct));
            db.Assignments.Add(assignment);
            await MathAssignmentAuthoringService.SaveAsync(db, assignment, request, ct);
            await db.SaveChangesAsync(ct);
            return Results.Json(await MathAssignmentAuthoringService.BuildEditDtoAsync(db, assignment, ct), JsonOptions());
        });

        app.MapGet("/api/math-assignments/{assignmentId:guid}/edit", async (
            Guid assignmentId,
            HttpContext http,
            IConfiguration cfg,
            TasksDbContext db,
            IHttpClientFactory clients,
            CancellationToken ct) =>
        {
            var loaded = await RequireTypedAssignmentForEditAsync(assignmentId, "math", http, cfg, db, clients, ct);
            if (loaded.Error is not null) return loaded.Error;
            return Results.Json(await MathAssignmentAuthoringService.BuildEditDtoAsync(db, loaded.Assignment!, ct), JsonOptions());
        });

        app.MapPut("/api/math-assignments/{assignmentId:guid}", async (
            Guid assignmentId,
            MathAssignmentAuthoringRequest request,
            HttpContext http,
            IConfiguration cfg,
            TasksDbContext db,
            IHttpClientFactory clients,
            CancellationToken ct) =>
        {
            var loaded = await RequireTypedAssignmentForEditAsync(assignmentId, "math", http, cfg, db, clients, ct, tracked: true);
            if (loaded.Error is not null) return loaded.Error;
            var assignment = loaded.Assignment!;
            var oldRating = assignment.Rating;
            var oldVisible = assignment.IsVisible;
            AssignmentAuthoringCommonService.ApplyMetadata(assignment, request);
            await MathAssignmentAuthoringService.SaveAsync(db, assignment, request, ct);
            await db.SaveChangesAsync(ct);
            await MarkTypedAssignmentChangeAsync(assignment, oldRating, oldVisible, clients, cfg, db, ct);
            return Results.Json(await MathAssignmentAuthoringService.BuildEditDtoAsync(db, assignment, ct), JsonOptions());
        });

        return app;
    }
}
