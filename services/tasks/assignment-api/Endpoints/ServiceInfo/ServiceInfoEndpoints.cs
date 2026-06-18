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
    private static WebApplication MapServiceInfoEndpoints(WebApplication app)
    {
        app.MapGet("/health/live", () => Results.Ok(new { status = "ok", service = "taskforge-tasks-api" }));

        app.MapGet("/health/ready", async (TasksDbContext db) => await db.Database.CanConnectAsync() ? Results.Ok(new { status = "ready", service = "taskforge-tasks-api" }) : Results.StatusCode(503));

        app.MapGet("/", () => Results.Ok(new { service = "taskforge-tasks-api", database = "taskforge_tasks", status = "tasks microservice active" }));

        app.MapGet("/api/tasks/assignment-api/schema-owner", () => Results.Ok(new { database = "taskforge_tasks", ownedEntities = new[] { "Assignment", "TaskAttempt" } }));

        return app;
    }
}
