using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using TaskForge.Realtime;
using TaskForge.Tasks.Api.Data;
using TaskForge.Tasks.Api.Services.Runtime;
using static TaskForge.Tasks.Api.Services.Common.AssignmentApiCommonService;
using static TaskForge.Tasks.Api.Services.Math.AssignmentApiMathService;
using static TaskForge.Tasks.Api.Services.Results.AssignmentApiResultsService;
using static TaskForge.Tasks.Api.Services.Serialization.AssignmentApiSerializationService;

namespace TaskForge.Tasks.Api.Endpoints;

internal static partial class AssignmentApiEndpoints
{
    private static WebApplication MapMathAssignmentRuntimeEndpoints(WebApplication app)
    {
        app.MapGet("/api/math-assignments/{assignmentId:guid}", async (
            Guid assignmentId, HttpContext http, IConfiguration cfg, TasksDbContext db, IHttpClientFactory clients, CancellationToken ct) =>
        {
            var loaded = await RequireTypedAssignmentForRuntimeAsync(assignmentId, "math", http, cfg, db, clients, ct);
            if (loaded.Error is not null) return loaded.Error;
            var solved = await IsSolvedForCurrentUserAsync(loaded.Assignment!, http, cfg, db, clients, ct);
            return Results.Json(await AssignmentTypedReadService.BuildDtoAsync(db, loaded.Assignment!, loaded.IncludeSensitive, solved, ct), JsonOptions());
        });

        app.MapGet("/api/math-assignments/{assignmentId:guid}/solve-shell", async (
            Guid assignmentId, HttpContext http, IConfiguration cfg, TasksDbContext db, IHttpClientFactory clients, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            var loaded = await RequireTypedAssignmentForRuntimeAsync(assignmentId, "math", http, cfg, db, clients, ct);
            if (loaded.Error is not null) return loaded.Error;
            var solved = await IsSolvedForCurrentUserAsync(loaded.Assignment!, http, cfg, db, clients, ct);
            return Results.Json(await AssignmentTypedReadService.BuildSolveShellAsync(db, loaded.Assignment!, loaded.IncludeSensitive, solved, "/api/math-assignments", ct), JsonOptions());
        });

        app.MapGet("/api/math-assignments/{assignmentId:guid}/statement", async (
            Guid assignmentId, HttpContext http, IConfiguration cfg, TasksDbContext db, IHttpClientFactory clients, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            var loaded = await RequireTypedAssignmentForRuntimeAsync(assignmentId, "math", http, cfg, db, clients, ct);
            if (loaded.Error is not null) return loaded.Error;
            return Results.Json(await AssignmentTypedReadService.BuildStatementAsync(db, loaded.Assignment!, loaded.IncludeSensitive, ct), JsonOptions());
        });

        app.MapGet("/api/math-assignments/{assignmentId:guid}/tests", async (
            Guid assignmentId, HttpContext http, IConfiguration cfg, TasksDbContext db, IHttpClientFactory clients, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            var loaded = await RequireTypedAssignmentForRuntimeAsync(assignmentId, "math", http, cfg, db, clients, ct);
            if (loaded.Error is not null) return loaded.Error;
            return Results.Json(await AssignmentTypedReadService.BuildTestsAsync(db, loaded.Assignment!, loaded.IncludeSensitive, ct), JsonOptions());
        });

        app.MapPost("/api/math-assignments/{assignmentId:guid}/attempts", async (
            Guid assignmentId, HttpContext http, IConfiguration cfg, TasksDbContext db, IHttpClientFactory clients, CancellationToken ct) =>
            await StartMath(assignmentId, http, cfg, db, clients, ct));

        app.MapPost("/api/math-assignments/{assignmentId:guid}/attempts/{attemptId:guid}/submit", async (
            Guid assignmentId, Guid attemptId, JsonElement payload, HttpContext http, IConfiguration cfg, TasksDbContext db, IHttpClientFactory clients, AdminSolutionEventPublisher live, CancellationToken ct) =>
        {
            if (CheckUserRateLimit(http, cfg, "task-submit") is { } limited) return limited;
            var normalized = WithAttemptId(payload, attemptId);
            return await SubmitMath(assignmentId, normalized, http, cfg, db, clients, live, ct);
        });

        app.MapGet("/api/math-assignments/{assignmentId:guid}/attempts/{attemptId:guid}", async (
            Guid assignmentId, Guid attemptId, HttpContext http, IConfiguration cfg, TasksDbContext db, CancellationToken ct) =>
        {
            var attemptMatches = await db.Attempts.AsNoTracking().AnyAsync(
                x => x.Id == attemptId && x.TaskAssignmentId == assignmentId && x.Kind == "math", ct);
            if (!attemptMatches) return Results.NotFound(new { message = "Попытка не найдена.", code = "ATTEMPT_NOT_FOUND" });
            return await ReviewAttempt(attemptId, "math", TaskForgeRequestSecurity.UserId(http, cfg), false, db);
        });

        return app;
    }
}
