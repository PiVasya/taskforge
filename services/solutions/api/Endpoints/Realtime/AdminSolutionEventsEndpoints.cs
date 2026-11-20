using TaskForge.Realtime;
using TaskForge.Solutions.Api.Data;
using static TaskForge.Solutions.Api.Services.Access.SolutionsApiAccessService;

namespace TaskForge.Solutions.Api.Endpoints;

internal static partial class SolutionsApiEndpoints
{
    private static WebApplication MapAdminSolutionEventsEndpoints(WebApplication app)
    {
        // All five attempt kinds are persisted transactionally in Solutions.
        async Task Stream(HttpContext http, IConfiguration cfg, SolutionsDbContext db, ILoggerFactory logs, CancellationToken ct)
        {
            if (!IsAdmin(http, cfg))
            {
                http.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            await AdminSolutionFeedSse.StreamAsync(http, db,
                logs.CreateLogger("AdminSolutionFeed.Solutions"), ct);
        }
        app.MapGet("/api/admin/solution-events", Stream);
        // Keep the previous public SSE URL while routing both feeds to one owner.
        app.MapGet("/api/admin/task-solution-events", Stream);
        return app;
    }
}
