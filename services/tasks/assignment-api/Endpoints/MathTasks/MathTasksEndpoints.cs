using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using TaskForge.Tasks.Api.Data;
using TaskForge.Tasks.Api.Domain;

using TaskForge.Tasks.Api.Contracts;
using static TaskForge.Tasks.Api.Services.Access.AssignmentApiAccessService;
using static TaskForge.Tasks.Api.Services.Common.AssignmentApiCommonService;
using static TaskForge.Tasks.Api.Services.Image.AssignmentApiImageService;
using static TaskForge.Tasks.Api.Services.Mapping.AssignmentApiMappingService;
using static TaskForge.Tasks.Api.Services.Math.AssignmentApiMathService;
using static TaskForge.Tasks.Api.Services.Results.AssignmentApiResultsService;
using static TaskForge.Tasks.Api.Services.Serialization.AssignmentApiSerializationService;
using static TaskForge.Tasks.Api.Services.Testing.AssignmentApiTestingService;

namespace TaskForge.Tasks.Api.Endpoints;

internal static partial class AssignmentApiEndpoints
{
    private static WebApplication MapMathTasksEndpoints(WebApplication app)
    {
        app.MapGet("/api/math-tasks/{assignmentId:guid}/edit", async (Guid assignmentId, HttpContext http, IConfiguration cfg, TasksDbContext db) =>
        {
            if (!IsEditor(http, cfg)) return Microsoft.AspNetCore.Http.Results.Json(new { message = "Для редактирования math-задания нужны права редактора.", code = "EDITOR_REQUIRED" }, statusCode: StatusCodes.Status403Forbidden);
            var assignment = await db.Assignments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == assignmentId);
            if (assignment == null) return Microsoft.AspNetCore.Http.Results.NotFound(new { message = "Задание не найдено.", code = "ASSIGNMENT_NOT_FOUND" });
            return Microsoft.AspNetCore.Http.Results.Ok(MathSpecToJsonObject(ReadMathSpec(assignment)));
        });

        app.MapPut("/api/math-tasks/{assignmentId:guid}/edit", async (Guid assignmentId, JsonElement payload, HttpContext http, IConfiguration cfg, TasksDbContext db) => IsEditor(http, cfg) ? await SaveSpec(assignmentId, payload, db, kind: "math") : Microsoft.AspNetCore.Http.Results.Json(new { message = "Для редактирования math-задания нужны права редактора.", code = "EDITOR_REQUIRED" }, statusCode: StatusCodes.Status403Forbidden));

        app.MapPost("/api/math-tasks/{assignmentId:guid}/start", async (Guid assignmentId, HttpContext http, IConfiguration cfg, TasksDbContext db, IHttpClientFactory clients, CancellationToken ct) => await StartMath(assignmentId, http, cfg, db, clients, ct));

        app.MapPost("/api/math-tasks/{assignmentId:guid}/submit", async (Guid assignmentId, JsonElement payload, HttpContext http, IConfiguration cfg, TasksDbContext db, IHttpClientFactory clients, CancellationToken ct) =>
        {
            if (CheckUserRateLimit(http, "task-submit") is { } limited) return limited;
            return await SubmitMath(assignmentId, payload, http, cfg, db, clients, ct);
        });

        app.MapGet("/api/me/math-attempts", async (HttpContext http, IConfiguration cfg, TasksDbContext db, Guid? courseId, Guid? assignmentId, int? days, int skip = 0, int take = 50) =>
            Microsoft.AspNetCore.Http.Results.Ok(await ListAttempts("math", TaskForgeRequestSecurity.UserId(http, cfg), courseId, assignmentId, days, skip, take, db)));

        app.MapGet("/api/me/math-attempts/{attemptId:guid}", async (Guid attemptId, HttpContext http, IConfiguration cfg, TasksDbContext db) => await ReviewAttempt(attemptId, "math", TaskForgeRequestSecurity.UserId(http, cfg), false, db));

        app.MapGet("/api/admin/users/{userId:guid}/math-attempts", async (Guid userId, TasksDbContext db, Guid? courseId, Guid? assignmentId, int? days, int skip = 0, int take = 50) => Microsoft.AspNetCore.Http.Results.Ok(await ListAttempts("math", userId, courseId, assignmentId, days, skip, take, db)));

        app.MapGet("/api/admin/math-attempts/{attemptId:guid}", async (Guid attemptId, TasksDbContext db) => await ReviewAttempt(attemptId, "math", null, true, db));

        app.MapDelete("/api/admin/math-attempts/{attemptId:guid}", async (Guid attemptId, TasksDbContext db) => await DeleteAttempt(attemptId, "math", db));

        return app;
    }
}
