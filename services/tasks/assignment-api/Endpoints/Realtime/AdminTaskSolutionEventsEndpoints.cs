using TaskForge.Realtime;
using TaskForge.Tasks.Api.Data;

namespace TaskForge.Tasks.Api.Endpoints;

internal static partial class AssignmentApiEndpoints
{
    private static WebApplication MapAdminTaskSolutionEventsEndpoints(WebApplication app)
    {
        // Test and math attempts, persisted transactionally in taskforge_k3s_tasks.
        app.MapGet("/api/admin/task-solution-events", async (
            HttpContext http, IConfiguration cfg, TasksDbContext db, ILoggerFactory logs, CancellationToken ct) =>
        {
            var principal = TaskForgeRequestSecurity.ValidateUser(http, cfg);
            if (principal is null || !TaskForgeRequestSecurity.HasAnyRole(principal, "Admin"))
            {
                http.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            await AdminSolutionFeedSse.StreamAsync(http, db,
                logs.CreateLogger("AdminSolutionFeed.Tasks"), ct);
        });
        return app;
    }
}
