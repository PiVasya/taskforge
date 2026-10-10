using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using TaskForge.Tasks.Api.Data;
using TaskForge.Tasks.Api.Domain;
using TaskForge.Realtime;

using TaskForge.Tasks.Api.Contracts;
using TaskForge.Tasks.Api.Services.Testing;
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
    private static WebApplication MapTaskTestsEndpoints(WebApplication app)
    {
        app.MapGet("/api/me/test-attempts", async (HttpContext http, IConfiguration cfg, TasksDbContext db, Guid? courseId, Guid? assignmentId, int? days, int skip = 0, int take = 50) =>
            Microsoft.AspNetCore.Http.Results.Ok(await ListAttempts("test", TaskForgeRequestSecurity.UserId(http, cfg), courseId, assignmentId, days, skip, take, db)));

        app.MapGet("/api/me/test-attempts/{attemptId:guid}", async (Guid attemptId, HttpContext http, IConfiguration cfg, TasksDbContext db) => await ReviewAttempt(attemptId, "test", TaskForgeRequestSecurity.UserId(http, cfg), false, db));

        app.MapGet("/api/admin/users/{userId:guid}/test-attempts", async (Guid userId, HttpContext http, TasksDbContext db, Guid? courseId, Guid? assignmentId, int? days, int skip = 0, int take = 50, CancellationToken ct = default) =>
        {
            var total = await CountAttempts("test", userId, courseId, assignmentId, days, db, ct);
            http.Response.Headers["X-Total-Count"] = total.ToString(System.Globalization.CultureInfo.InvariantCulture);
            http.Response.Headers["X-Result-User-Id"] = userId.ToString("D");
            return Microsoft.AspNetCore.Http.Results.Ok(await ListAttempts("test", userId, courseId, assignmentId, days, skip, take, db));
        });

        app.MapGet("/api/admin/test-attempts/{attemptId:guid}", async (Guid attemptId, TasksDbContext db) => await ReviewAttempt(attemptId, "test", null, true, db));

        app.MapDelete("/api/admin/test-attempts/{attemptId:guid}", async (Guid attemptId, TasksDbContext db, IHttpClientFactory clients, IConfiguration cfg, CancellationToken ct) => await DeleteAttempt(attemptId, "test", db, clients, cfg, ct));

        app.MapDelete("/api/admin/users/{userId:guid}/test-attempts", async (Guid userId, TasksDbContext db, IHttpClientFactory clients, IConfiguration cfg, CancellationToken ct) =>
            await DeleteUserAttempts(userId, "test", db, clients, cfg, ct));

        return app;
    }
}
