using System.Text.Json.Nodes;

namespace TaskForge.Ai.Api.Endpoints;

internal static partial class AiApiEndpoints
{
    private static WebApplication MapAdminInvestigationEndpoints(WebApplication app)
    {
        app.MapGet("/api/admin/ai/investigation", () => Microsoft.AspNetCore.Http.Results.Ok(new
        {
            purpose = "Read-only support investigation API for administrators and AI agents operating with an Admin session.",
            browserPolicy = "Use these APIs to read support/activity/solutions. Use Browser API only when visual reproduction is required.",
            limits = new { maxTake = 500, defaultWindowHours = 24 },
            endpoints = new
            {
                support = "/api/admin/ai/investigation/support/{ticketId}?fromUtc=&toUtc=&take=&assignmentId=",
                activity = "/api/admin/ai/investigation/users/{userId}/activity?fromUtc=&toUtc=&take=",
                solutions = "/api/admin/ai/investigation/users/{userId}/solutions?assignmentId=&fromUtc=&toUtc=&take=",
                solution = "/api/admin/ai/investigation/solutions/{kind}/{itemId}",
                assignment = "/api/admin/ai/investigation/assignments/{assignmentId}"
            }
        }));

        app.MapGet("/api/admin/ai/investigation/support/{ticketId:guid}", async (
            Guid ticketId,
            Guid? assignmentId,
            DateTimeOffset? fromUtc,
            DateTimeOffset? toUtc,
            int? take,
            IHttpClientFactory httpFactory,
            IConfiguration cfg,
            CancellationToken ct) =>
        {
            var limit = System.Math.Clamp(take ?? 200, 1, 500);
            var to = toUtc ?? DateTimeOffset.UtcNow;
            var from = fromUtc ?? to.AddHours(-24);
            try
            {
                var ticket = await GetInternalJsonAsync(httpFactory, cfg, $"{ServiceBase(cfg, "SupportApi", "http://support-api:8080")}/api/internal/agent/support/{ticketId:D}", ct);
                var userId = ReadGuid(ticket, "userId");
                if (!userId.HasValue || userId == Guid.Empty)
                    return Microsoft.AspNetCore.Http.Results.Json(new { message = "У обращения нет пользователя.", code = "SUPPORT_USER_MISSING" }, statusCode: StatusCodes.Status409Conflict);

                var activityTask = GetInternalJsonAsync(httpFactory, cfg, BuildTasksActivityUrl(cfg, userId.Value, from, to, limit), ct);
                var codeTask = GetInternalJsonAsync(httpFactory, cfg, BuildSolutionsIndexUrl(cfg, userId.Value, assignmentId, from, to, limit), ct);
                var attemptsTask = GetInternalJsonAsync(httpFactory, cfg, BuildAttemptsIndexUrl(cfg, userId.Value, assignmentId, from, to, limit), ct);
                await Task.WhenAll(activityTask, codeTask, attemptsTask);

                var solutions = BuildCombinedSolutionIndex(codeTask.Result, attemptsTask.Result, limit);
                return Microsoft.AspNetCore.Http.Results.Ok(new
                {
                    ticketId,
                    userId,
                    assignmentId,
                    window = new { fromUtc = from, toUtc = to },
                    ticket,
                    activity = activityTask.Result,
                    solutions,
                    timeline = BuildInvestigationTimeline(ticket, activityTask.Result, solutions, System.Math.Clamp(limit * 2, 1, 500))
                });
            }
            catch (InternalToolHttpException ex)
            {
                return InvestigationUpstreamFailed(ex);
            }
        });

        app.MapGet("/api/admin/ai/investigation/users/{userId:guid}/activity", async (
            Guid userId,
            DateTimeOffset? fromUtc,
            DateTimeOffset? toUtc,
            int? take,
            IHttpClientFactory httpFactory,
            IConfiguration cfg,
            CancellationToken ct) =>
        {
            var limit = System.Math.Clamp(take ?? 200, 1, 500);
            var to = toUtc ?? DateTimeOffset.UtcNow;
            var from = fromUtc ?? to.AddHours(-24);
            try
            {
                var payload = await GetInternalJsonAsync(httpFactory, cfg, BuildTasksActivityUrl(cfg, userId, from, to, limit), ct);
                return Microsoft.AspNetCore.Http.Results.Ok(payload);
            }
            catch (InternalToolHttpException ex)
            {
                return InvestigationUpstreamFailed(ex);
            }
        });

        app.MapGet("/api/admin/ai/investigation/users/{userId:guid}/solutions", async (
            Guid userId,
            Guid? assignmentId,
            DateTimeOffset? fromUtc,
            DateTimeOffset? toUtc,
            int? take,
            IHttpClientFactory httpFactory,
            IConfiguration cfg,
            CancellationToken ct) =>
        {
            var limit = System.Math.Clamp(take ?? 200, 1, 500);
            var to = toUtc ?? DateTimeOffset.UtcNow;
            var from = fromUtc ?? to.AddDays(-7);
            try
            {
                var codeTask = GetInternalJsonAsync(httpFactory, cfg, BuildSolutionsIndexUrl(cfg, userId, assignmentId, from, to, limit), ct);
                var attemptsTask = GetInternalJsonAsync(httpFactory, cfg, BuildAttemptsIndexUrl(cfg, userId, assignmentId, from, to, limit), ct);
                await Task.WhenAll(codeTask, attemptsTask);
                return Microsoft.AspNetCore.Http.Results.Ok(new
                {
                    userId,
                    assignmentId,
                    window = new { fromUtc = from, toUtc = to },
                    solutions = BuildCombinedSolutionIndex(codeTask.Result, attemptsTask.Result, limit)
                });
            }
            catch (InternalToolHttpException ex)
            {
                return InvestigationUpstreamFailed(ex);
            }
        });

        app.MapGet("/api/admin/ai/investigation/solutions/{kind}/{itemId:guid}", async (
            string kind,
            Guid itemId,
            IHttpClientFactory httpFactory,
            IConfiguration cfg,
            CancellationToken ct) =>
        {
            var normalized = (kind ?? string.Empty).Trim().ToLowerInvariant();
            if (normalized is not ("code" or "sql" or "image" or "test" or "math"))
                return Microsoft.AspNetCore.Http.Results.BadRequest(new { message = "kind должен быть code, sql, image, test или math.", code = "AI_TOOL_KIND_INVALID" });
            try
            {
                var url = normalized is "test" or "math"
                    ? $"{ServiceBase(cfg, "TasksApi", "http://tasks-api:8080")}/api/internal/agent/attempts/{Uri.EscapeDataString(normalized)}/{itemId:D}"
                    : $"{ServiceBase(cfg, "SolutionsApi", "http://solutions-api:8080")}/api/internal/agent/solutions/{Uri.EscapeDataString(normalized)}/{itemId:D}";
                var detail = await GetInternalJsonAsync(httpFactory, cfg, url, ct);
                return Microsoft.AspNetCore.Http.Results.Ok(new { kind = normalized, itemId, detail });
            }
            catch (InternalToolHttpException ex)
            {
                return InvestigationUpstreamFailed(ex);
            }
        });

        app.MapGet("/api/admin/ai/investigation/assignments/{assignmentId:guid}", async (
            Guid assignmentId,
            IHttpClientFactory httpFactory,
            IConfiguration cfg,
            CancellationToken ct) =>
        {
            try
            {
                var assignment = await GetInternalJsonAsync(httpFactory, cfg, $"{ServiceBase(cfg, "TasksApi", "http://tasks-api:8080")}/api/internal/agent/assignments/{assignmentId:D}", ct);
                return Microsoft.AspNetCore.Http.Results.Ok(assignment);
            }
            catch (InternalToolHttpException ex)
            {
                return InvestigationUpstreamFailed(ex);
            }
        });

        return app;
    }

    private static IResult InvestigationUpstreamFailed(InternalToolHttpException ex) => Microsoft.AspNetCore.Http.Results.Json(new
    {
        message = "Внутренний сервис не смог вернуть данные для расследования.",
        code = "AI_INVESTIGATION_UPSTREAM_FAILED",
        upstreamStatus = (int)ex.StatusCode,
        upstream = ex.Service
    }, statusCode: StatusCodes.Status502BadGateway);
}
