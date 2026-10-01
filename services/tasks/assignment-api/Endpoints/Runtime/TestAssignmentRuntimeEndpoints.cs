using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using System.Text.Json.Nodes;
using TaskForge.Realtime;
using TaskForge.Tasks.Api.Data;
using TaskForge.Tasks.Api.Services.Runtime;
using static TaskForge.Tasks.Api.Services.Common.AssignmentApiCommonService;
using static TaskForge.Tasks.Api.Services.Results.AssignmentApiResultsService;
using static TaskForge.Tasks.Api.Services.Serialization.AssignmentApiSerializationService;
using static TaskForge.Tasks.Api.Services.Testing.AssignmentApiTestingService;

namespace TaskForge.Tasks.Api.Endpoints;

internal static partial class AssignmentApiEndpoints
{
    private static WebApplication MapTestAssignmentRuntimeEndpoints(WebApplication app)
    {
        app.MapGet("/api/test-assignments/{assignmentId:guid}", async (
            Guid assignmentId, HttpContext http, IConfiguration cfg, TasksDbContext db, IHttpClientFactory clients, CancellationToken ct) =>
        {
            var loaded = await RequireTypedAssignmentForRuntimeAsync(assignmentId, "test", http, cfg, db, clients, ct);
            if (loaded.Error is not null) return loaded.Error;
            var solved = await IsSolvedForCurrentUserAsync(loaded.Assignment!, http, cfg, db, clients, ct);
            return Results.Json(await AssignmentTypedReadService.BuildDtoAsync(db, loaded.Assignment!, loaded.IncludeSensitive, solved, ct), JsonOptions());
        });

        app.MapGet("/api/test-assignments/{assignmentId:guid}/solve-shell", async (
            Guid assignmentId, HttpContext http, IConfiguration cfg, TasksDbContext db, IHttpClientFactory clients, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            var loaded = await RequireTypedAssignmentForRuntimeAsync(assignmentId, "test", http, cfg, db, clients, ct);
            if (loaded.Error is not null) return loaded.Error;
            var solved = await IsSolvedForCurrentUserAsync(loaded.Assignment!, http, cfg, db, clients, ct);
            return Results.Json(await AssignmentTypedReadService.BuildSolveShellAsync(db, loaded.Assignment!, loaded.IncludeSensitive, solved, "/api/test-assignments", ct), JsonOptions());
        });

        app.MapGet("/api/test-assignments/{assignmentId:guid}/statement", async (
            Guid assignmentId, HttpContext http, IConfiguration cfg, TasksDbContext db, IHttpClientFactory clients, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            var loaded = await RequireTypedAssignmentForRuntimeAsync(assignmentId, "test", http, cfg, db, clients, ct);
            if (loaded.Error is not null) return loaded.Error;
            return Results.Json(await AssignmentTypedReadService.BuildStatementAsync(db, loaded.Assignment!, loaded.IncludeSensitive, ct), JsonOptions());
        });

        app.MapGet("/api/test-assignments/{assignmentId:guid}/tests", async (
            Guid assignmentId, HttpContext http, IConfiguration cfg, TasksDbContext db, IHttpClientFactory clients, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            var loaded = await RequireTypedAssignmentForRuntimeAsync(assignmentId, "test", http, cfg, db, clients, ct);
            if (loaded.Error is not null) return loaded.Error;
            return Results.Json(await AssignmentTypedReadService.BuildTestsAsync(db, loaded.Assignment!, loaded.IncludeSensitive, ct), JsonOptions());
        });

        app.MapPost("/api/test-assignments/{assignmentId:guid}/attempts", async (
            Guid assignmentId, HttpContext http, IConfiguration cfg, TasksDbContext db, IHttpClientFactory clients, CancellationToken ct) =>
            await StartTest(assignmentId, http, cfg, db, clients, ct));

        app.MapPost("/api/test-assignments/{assignmentId:guid}/attempts/{attemptId:guid}/submit", async (
            Guid assignmentId, Guid attemptId, JsonElement payload, HttpContext http, IConfiguration cfg, TasksDbContext db, IHttpClientFactory clients, AdminSolutionEventPublisher live, CancellationToken ct) =>
        {
            if (CheckUserRateLimit(http, cfg, "task-submit") is { } limited) return limited;
            var normalized = WithAttemptId(payload, attemptId);
            return await SubmitTest(assignmentId, normalized, http, cfg, db, clients, live, ct);
        });

        app.MapGet("/api/test-assignments/{assignmentId:guid}/attempts/{attemptId:guid}", async (
            Guid assignmentId, Guid attemptId, HttpContext http, IConfiguration cfg, TasksDbContext db, CancellationToken ct) =>
        {
            var attemptMatches = await db.Attempts.AsNoTracking().AnyAsync(
                x => x.Id == attemptId && x.TaskAssignmentId == assignmentId && x.Kind == "test", ct);
            if (!attemptMatches) return Results.NotFound(new { message = "Попытка не найдена.", code = "ATTEMPT_NOT_FOUND" });
            return await ReviewAttempt(attemptId, "test", TaskForgeRequestSecurity.UserId(http, cfg), false, db);
        });

        return app;
    }

    private static JsonElement WithAttemptId(JsonElement payload, Guid attemptId)
    {
        var node = payload.ValueKind == JsonValueKind.Object
            ? JsonNode.Parse(payload.GetRawText()) as JsonObject ?? new JsonObject()
            : new JsonObject();
        node["attemptId"] = attemptId.ToString("D");
        return JsonSerializer.SerializeToElement(node, JsonOptions());
    }
}
