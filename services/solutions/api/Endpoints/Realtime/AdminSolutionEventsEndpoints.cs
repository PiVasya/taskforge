using TaskForge.Realtime;
using TaskForge.Solutions.Api.Data;
using static TaskForge.Solutions.Api.Services.Access.SolutionsApiAccessService;

namespace TaskForge.Solutions.Api.Endpoints;

internal static partial class SolutionsApiEndpoints
{
    private static WebApplication MapAdminSolutionEventsEndpoints(WebApplication app)
    {
        // Code, SQL and images, persisted transactionally in taskforge_k3s_solutions.
        app.MapGet("/api/admin/solution-events", async (
            HttpContext http, IConfiguration cfg, SolutionsDbContext db, ILoggerFactory logs, CancellationToken ct) =>
        {
            if (!IsAdmin(http, cfg))
            {
                http.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            await AdminSolutionFeedSse.StreamAsync(http, db,
                logs.CreateLogger("AdminSolutionFeed.Solutions"), ct);
        });
        return app;
    }
}
