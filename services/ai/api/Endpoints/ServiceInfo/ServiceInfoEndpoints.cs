using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using TaskForge.Ai.Api.Data;
using TaskForge.Ai.Api.Domain;

using TaskForge.Ai.Api.Contracts;
using TaskForge.Ai.Api.Hubs;
using static TaskForge.Ai.Api.Services.Access.AiApiAccessService;
using static TaskForge.Ai.Api.Services.Common.AiApiCommonService;
using static TaskForge.Ai.Api.Services.Mapping.AiApiMappingService;
using static TaskForge.Ai.Api.Services.Results.AiApiResultsService;
using static TaskForge.Ai.Api.Services.Serialization.AiApiSerializationService;
using static TaskForge.Ai.Api.Services.Testing.AiApiTestingService;

namespace TaskForge.Ai.Api.Endpoints;

internal static partial class AiApiEndpoints
{
    private static WebApplication MapServiceInfoEndpoints(WebApplication app)
    {
        app.MapGet("/health/live", () => Microsoft.AspNetCore.Http.Results.Ok(new { status = "ok", service = "taskforge-ai-api" }));

        app.MapGet("/health/ready", async (AiDbContext db) =>
        {
            var canConnect = await db.Database.CanConnectAsync();
            return canConnect ? Microsoft.AspNetCore.Http.Results.Ok(new { status = "ready", service = "taskforge-ai-api" }) : Microsoft.AspNetCore.Http.Results.StatusCode(503);
        });

        app.MapGet("/", () => Microsoft.AspNetCore.Http.Results.Ok(new
        {
            service = "taskforge-ai-api",
            database = "taskforge_ai",
            migrations = "tracked EF Core migrations",
            status = "microservice boundary active"
        }));

        app.MapGet("/api/ai/api/schema-owner", () => Microsoft.AspNetCore.Http.Results.Ok(new
        {
            database = "taskforge_ai",
            ownedEntities = new[] { "AgentConversation", "AgentMessage", "AgentRun", "AgentStep", "AgentRunArtifact", "PromptTemplate" }
        }));

        return app;
    }
}
