using TaskForge.Tasks.Api.Contracts;
using TaskForge.Tasks.Api.Data;
using TaskForge.Tasks.Api.Services.Authoring;
using static TaskForge.Tasks.Api.Services.Access.AssignmentApiAccessService;
using static TaskForge.Tasks.Api.Services.Results.AssignmentApiResultsService;
using static TaskForge.Tasks.Api.Services.Serialization.AssignmentApiSerializationService;

namespace TaskForge.Tasks.Api.Endpoints;

internal static partial class AssignmentApiEndpoints
{
    private static WebApplication MapTestAssignmentAuthoringEndpoints(WebApplication app)
    {
        app.MapPost("/api/courses/{courseId:guid}/test-assignments", async (
            Guid courseId,
            TestAssignmentAuthoringRequest request,
            HttpContext http,
            IConfiguration cfg,
            TasksDbContext db,
            IHttpClientFactory clients,
            CancellationToken ct) =>
        {
            if (!await CanUserEditCourseAsync(courseId, http, cfg, clients, ct)) return CourseEditForbidden();
            var assignment = AssignmentAuthoringCommonService.BuildBase(courseId, "test", request, await NextAssignmentSortAsync(db, courseId, ct));
            db.Assignments.Add(assignment);
            await TestAssignmentAuthoringService.SaveAsync(db, assignment, request, ct);
            await db.SaveChangesAsync(ct);
            return Results.Json(await TestAssignmentAuthoringService.BuildEditDtoAsync(db, assignment, ct), JsonOptions());
        });

        app.MapGet("/api/test-assignments/{assignmentId:guid}/edit", async (
            Guid assignmentId,
            HttpContext http,
            IConfiguration cfg,
            TasksDbContext db,
            IHttpClientFactory clients,
            CancellationToken ct) =>
        {
            var loaded = await RequireTypedAssignmentForEditAsync(assignmentId, "test", http, cfg, db, clients, ct);
            if (loaded.Error is not null) return loaded.Error;
            return Results.Json(await TestAssignmentAuthoringService.BuildEditDtoAsync(db, loaded.Assignment!, ct), JsonOptions());
        });

        app.MapPut("/api/test-assignments/{assignmentId:guid}", async (
            Guid assignmentId,
            TestAssignmentAuthoringRequest request,
            HttpContext http,
            IConfiguration cfg,
            TasksDbContext db,
            IHttpClientFactory clients,
            CancellationToken ct) =>
        {
            var loaded = await RequireTypedAssignmentForEditAsync(assignmentId, "test", http, cfg, db, clients, ct, tracked: true);
            if (loaded.Error is not null) return loaded.Error;
            var assignment = loaded.Assignment!;
            var oldRating = assignment.Rating;
            var oldVisible = assignment.IsVisible;
            AssignmentAuthoringCommonService.ApplyMetadata(assignment, request);
            await TestAssignmentAuthoringService.SaveAsync(db, assignment, request, ct);
            await db.SaveChangesAsync(ct);
            await MarkTypedAssignmentChangeAsync(assignment, oldRating, oldVisible, clients, cfg, db, ct);
            return Results.Json(await TestAssignmentAuthoringService.BuildEditDtoAsync(db, assignment, ct), JsonOptions());
        });

        return app;
    }
}
