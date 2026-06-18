using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using TaskForge.Education.Api.Data;
using TaskForge.Education.Api.Domain;

using TaskForge.Education.Api.Contracts;
using static TaskForge.Education.Api.Services.Access.EducationApiAccessService;
using static TaskForge.Education.Api.Services.Common.EducationApiCommonService;
using static TaskForge.Education.Api.Services.Mapping.EducationApiMappingService;
using static TaskForge.Education.Api.Services.Serialization.EducationApiSerializationService;

namespace TaskForge.Education.Api.Endpoints;

internal static partial class EducationApiEndpoints
{
    private static WebApplication MapServiceInfoEndpoints(WebApplication app)
    {
        app.MapGet("/health/live", () => Results.Ok(new { status = "ok", service = "taskforge-education-api" }));

        app.MapGet("/health/ready", async (EducationDbContext db) =>
        {
            var canConnect = await db.Database.CanConnectAsync();
            return canConnect ? Results.Ok(new { status = "ready", service = "taskforge-education-api" }) : Results.StatusCode(503);
        });

        app.MapGet("/", () => Results.Ok(new { service = "taskforge-education-api", database = "taskforge_education", status = "education microservice active" }));

        app.MapGet("/api/education/schema-owner", () => Results.Ok(new { database = "taskforge_education", ownedEntities = new[] { "Course", "Group", "GroupMember" } }));

        return app;
    }
}
