using Microsoft.EntityFrameworkCore;
using TaskForge.Ai.Api.Data;

namespace TaskForge.Ai.Api.Endpoints;

internal static partial class AiApiEndpoints
{
    private static WebApplication MapServiceInfoEndpoints(WebApplication app)
    {
        app.MapGet("/health/live", () => Results.Ok(new { status = "ok", service = "taskforge-account-intelligence" }));
        app.MapGet("/health/ready", async (AiDbContext db) =>
        {
            var canConnect = await db.Database.CanConnectAsync();
            return canConnect ? Results.Ok(new { status = "ready", service = "taskforge-account-intelligence" }) : Results.StatusCode(503);
        });
        app.MapGet("/", () => Results.Ok(new
        {
            service = "taskforge-account-intelligence",
            database = "taskforge_ai",
            migrations = "tracked EF Core migrations",
            status = "account manager active"
        }));
        app.MapGet("/api/ai/api/schema-owner", () => Results.Ok(new
        {
            database = "taskforge_ai",
            ownedEntities = new[] { "AccountAnalysisRun", "AccountAnalysisFinding", "AccountAnalysisReview", "AccountManagementOperation" }
        }));
        return app;
    }
}
