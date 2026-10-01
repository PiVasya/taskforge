using TaskForge.Tasks.Api.Contracts;
using TaskForge.Tasks.Api.Data;
using TaskForge.Tasks.Api.Services.Runtime;
using static TaskForge.Tasks.Api.Services.Serialization.AssignmentApiSerializationService;

namespace TaskForge.Tasks.Api.Endpoints;

internal static partial class AssignmentApiEndpoints
{
    private static WebApplication MapCodeAssignmentRuntimeEndpoints(WebApplication app)
    {
        app.MapGet("/api/code-assignments/{assignmentId:guid}", async (
            Guid assignmentId, HttpContext http, IConfiguration cfg, TasksDbContext db, IHttpClientFactory clients, CancellationToken ct) =>
        {
            var loaded = await RequireTypedAssignmentForRuntimeAsync(assignmentId, "code-test", http, cfg, db, clients, ct);
            if (loaded.Error is not null) return loaded.Error;
            var solved = await IsSolvedForCurrentUserAsync(loaded.Assignment!, http, cfg, db, clients, ct);
            return Results.Json(await AssignmentTypedReadService.BuildDtoAsync(db, loaded.Assignment!, loaded.IncludeSensitive, solved, ct), JsonOptions());
        });

        app.MapGet("/api/code-assignments/{assignmentId:guid}/solve-shell", async (
            Guid assignmentId, HttpContext http, IConfiguration cfg, TasksDbContext db, IHttpClientFactory clients, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            var loaded = await RequireTypedAssignmentForRuntimeAsync(assignmentId, "code-test", http, cfg, db, clients, ct);
            if (loaded.Error is not null) return loaded.Error;
            var solved = await IsSolvedForCurrentUserAsync(loaded.Assignment!, http, cfg, db, clients, ct);
            return Results.Json(await AssignmentTypedReadService.BuildSolveShellAsync(db, loaded.Assignment!, loaded.IncludeSensitive, solved, "/api/code-assignments", ct), JsonOptions());
        });

        app.MapGet("/api/code-assignments/{assignmentId:guid}/statement", async (
            Guid assignmentId, HttpContext http, IConfiguration cfg, TasksDbContext db, IHttpClientFactory clients, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            var loaded = await RequireTypedAssignmentForRuntimeAsync(assignmentId, "code-test", http, cfg, db, clients, ct);
            if (loaded.Error is not null) return loaded.Error;
            return Results.Json(await AssignmentTypedReadService.BuildStatementAsync(db, loaded.Assignment!, loaded.IncludeSensitive, ct), JsonOptions());
        });

        app.MapGet("/api/code-assignments/{assignmentId:guid}/tests", async (
            Guid assignmentId, HttpContext http, IConfiguration cfg, TasksDbContext db, IHttpClientFactory clients, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            var loaded = await RequireTypedAssignmentForRuntimeAsync(assignmentId, "code-test", http, cfg, db, clients, ct);
            if (loaded.Error is not null) return loaded.Error;
            return Results.Json(await AssignmentTypedReadService.BuildTestsAsync(db, loaded.Assignment!, loaded.IncludeSensitive, ct), JsonOptions());
        });


        return app;
    }
}
