using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TaskForge.Observability.Api.Data;
using TaskForge.Observability.Api.Domain;

using TaskForge.Observability.Api.Contracts;
using static TaskForge.Observability.Api.Services.Common.ObservabilityApiCommonService;
using static TaskForge.Observability.Api.Services.Mapping.ObservabilityApiMappingService;
using static TaskForge.Observability.Api.Services.Serialization.ObservabilityApiSerializationService;

namespace TaskForge.Observability.Api.Endpoints;

internal static partial class ObservabilityApiEndpoints
{
    private static WebApplication MapServiceInfoEndpoints(WebApplication app)
    {
        app.MapGet("/health/live", () => Microsoft.AspNetCore.Http.Results.Ok(new { status = "ok", service = "taskforge-observability-api" }));

        app.MapGet("/health/ready", async (ObservabilityDbContext db) => await db.Database.CanConnectAsync() ? Microsoft.AspNetCore.Http.Results.Ok(new { status = "ready", service = "taskforge-observability-api" }) : Microsoft.AspNetCore.Http.Results.StatusCode(503));

        app.MapGet("/", () => Microsoft.AspNetCore.Http.Results.Ok(new { service = "taskforge-observability-api", database = "taskforge_observability", status = "observability microservice active" }));

        app.MapGet("/api/observability/schema-owner", () => Microsoft.AspNetCore.Http.Results.Ok(new { database = "taskforge_observability", ownedEntities = new[] { "PageView", "AuditLog" } }));

        return app;
    }
}
