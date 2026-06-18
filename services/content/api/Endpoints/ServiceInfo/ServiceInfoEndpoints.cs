using System.Security.Claims;
using System.Text;
using System.Text.Json;
using LearningContentService.Data;
using LearningContentService.Data.Entities;
using LearningContentService.DTO;
using LearningContentService.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using static LearningContentService.Services.Common.LearningContentCommonService;
using static LearningContentService.Services.Serialization.LearningContentSerializationService;

namespace LearningContentService.Endpoints;

internal static partial class LearningContentEndpoints
{
    private static WebApplication MapServiceInfoEndpoints(WebApplication app)
    {
        app.MapGet("/health/live", () => Results.Ok(new { status = "ok", service = "learning-content-service" }));

        app.MapGet("/health/ready", async (LearningDbContext db) =>
        {
            var canConnect = await db.Database.CanConnectAsync();
            return canConnect ? Results.Ok(new { status = "ready" }) : Results.StatusCode(503);
        });

        return app;
    }
}
