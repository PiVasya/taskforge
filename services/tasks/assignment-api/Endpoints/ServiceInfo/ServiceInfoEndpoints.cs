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
        app.MapGet("/health/live", () => Microsoft.AspNetCore.Http.Results.Ok(new { status = "ok", service = "taskforge-tasks-api" }));

        app.MapGet("/health/ready", async (TasksDbContext db, TaskForge.Tasks.Api.Services.Access.CourseMapProjectionService _) => await db.Database.CanConnectAsync() ? Microsoft.AspNetCore.Http.Results.Ok(new { status = "ready", service = "taskforge-tasks-api" }) : Microsoft.AspNetCore.Http.Results.StatusCode(503));

        app.MapGet("/", () => Microsoft.AspNetCore.Http.Results.Ok(new { service = "taskforge-tasks-api", database = "taskforge_tasks", status = "tasks microservice active" }));

        app.MapGet("/api/tasks/assignment-api/schema-owner", () => Microsoft.AspNetCore.Http.Results.Ok(new { database = "taskforge_tasks", ownedEntities = new[] { "Assignment", "TaskAttempt" } }));

        return app;
    }
}
