using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TaskForge.Execution.Api.Data;
using TaskForge.Execution.Api.Domain;

using TaskForge.Execution.Api.Contracts;
using static TaskForge.Execution.Api.Services.Mapping.ExecutionApiMappingService;
using static TaskForge.Execution.Api.Services.Results.ExecutionApiResultsService;
using static TaskForge.Execution.Api.Services.Serialization.ExecutionApiSerializationService;

namespace TaskForge.Execution.Api.Endpoints;

internal static partial class ExecutionApiEndpoints
{
    private static WebApplication MapServiceInfoEndpoints(WebApplication app)
    {
        app.MapGet("/health/live", () => Microsoft.AspNetCore.Http.Results.Ok(new { status = "ok", service = "taskforge-execution-api" }));

        app.MapGet("/health/ready", async (ExecutionDbContext db) => await db.Database.CanConnectAsync() ? Microsoft.AspNetCore.Http.Results.Ok(new { status = "ready", service = "taskforge-execution-api" }) : Microsoft.AspNetCore.Http.Results.StatusCode(503));

        app.MapGet("/", () => Microsoft.AspNetCore.Http.Results.Ok(new { service = "taskforge-execution-api", database = "taskforge_execution", status = "execution microservice active" }));

        app.MapGet("/api/execution/api/schema-owner", () => Microsoft.AspNetCore.Http.Results.Ok(new { database = "taskforge_execution", ownedEntities = new[] { "ExecutionJob", "ExecutionResult", "RunnerHeartbeat" } }));

        return app;
    }
}
