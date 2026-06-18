using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using TaskForge.Support.Api.Data;
using TaskForge.Support.Api.Domain;

using TaskForge.Support.Api.Contracts;
using TaskForge.Support.Api.Hubs;
using static TaskForge.Support.Api.Services.Common.SupportApiCommonService;
using static TaskForge.Support.Api.Services.Mapping.SupportApiMappingService;
using static TaskForge.Support.Api.Services.Serialization.SupportApiSerializationService;

namespace TaskForge.Support.Api.Endpoints;

internal static partial class SupportApiEndpoints
{
    private static WebApplication MapServiceInfoEndpoints(WebApplication app)
    {
        app.MapGet("/health/live", () => Results.Ok(new { status = "ok", service = "taskforge-support-api" }));

        app.MapGet("/health/ready", async (SupportDbContext db) => await db.Database.CanConnectAsync() ? Results.Ok(new { status = "ready", service = "taskforge-support-api" }) : Results.StatusCode(503));

        app.MapGet("/", () => Results.Ok(new { service = "taskforge-support-api", database = "taskforge_support", status = "support microservice active" }));

        app.MapGet("/api/support/schema-owner", () => Results.Ok(new { database = "taskforge_support", ownedEntities = new[] { "SupportTicket", "SupportMessage" } }));

        return app;
    }
}
