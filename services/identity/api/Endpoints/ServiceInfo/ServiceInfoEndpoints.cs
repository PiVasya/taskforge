using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.IdentityModel.Tokens;
using TaskForge.Identity.Api.Data;
using TaskForge.Identity.Api.Domain;

using TaskForge.Identity.Api.Contracts;
using static TaskForge.Identity.Api.Services.Access.IdentityApiAccessService;
using static TaskForge.Identity.Api.Services.Common.IdentityApiCommonService;
using static TaskForge.Identity.Api.Services.Image.IdentityApiImageService;
using static TaskForge.Identity.Api.Services.Mapping.IdentityApiMappingService;
using static TaskForge.Identity.Api.Services.Results.IdentityApiResultsService;
using static TaskForge.Identity.Api.Services.Serialization.IdentityApiSerializationService;

namespace TaskForge.Identity.Api.Endpoints;

internal static partial class IdentityApiEndpoints
{
    private static WebApplication MapServiceInfoEndpoints(WebApplication app)
    {
        app.MapGet("/health/live", () => Results.Ok(new { status = "ok", service = "taskforge-identity-api" }));

        app.MapGet("/health/ready", async (IdentityDbContext db) =>
        {
            var canConnect = await db.Database.CanConnectAsync();
            return canConnect ? Results.Ok(new { status = "ready", service = "taskforge-identity-api" }) : Results.StatusCode(503);
        });

        app.MapGet("/", () => Results.Ok(new
        {
            service = "taskforge-identity-api",
            database = "taskforge_identity",
            status = "identity microservice active",
            endpoints = new[] { "/api/auth/login", "/api/auth/register", "/api/profile", "/api/me/ui-settings" }
        }));

        app.MapGet("/api/identity/schema-owner", () => Results.Ok(new
        {
            database = "taskforge_identity",
            ownedEntities = new[] { "User", "UserUiSettings", "UserLoginLog" }
        }));

        return app;
    }
}
