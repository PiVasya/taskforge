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
    private static WebApplication MapActivityEndpoints(WebApplication app)
    {
        app.MapPost("/api/activity/page-view", async (PageViewRequest req, HttpContext http, IConfiguration cfg, ObservabilityDbContext db, CancellationToken ct) =>
        {
            var view = new PageView
            {
                UserId = TaskForgeRequestSecurity.UserId(http, cfg),
                Path = req.Path ?? req.Url ?? http.Request.Headers.Referer.ToString() ?? "/",
                Method = req.Method ?? "GET",
                Action = req.Action ?? "page-view",
                StatusCode = req.StatusCode,
                DurationMs = req.DurationMs,
                UserAgent = http.Request.Headers.UserAgent.ToString()
            };
            db.PageViews.Add(view);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { saved = true, view.Id });
        });

        app.MapGet("/api/admin/activity", async (
            ObservabilityDbContext db,
            IConfiguration cfg,
            IHttpClientFactory httpFactory,
            int days = 7,
            int page = 1,
            int pageSize = 50,
            string? q = null,
            string? query = null,
            string? category = null,
            string? actionType = null,
            string? source = null,
            Guid? userId = null,
            int? take = null,
            CancellationToken ct = default) =>
        {
            days = Math.Clamp(days, 1, 365);
            page = Math.Max(1, page);
            pageSize = Math.Clamp(take ?? pageSize, 10, 1000);

            var nowUtc = DateTimeOffset.UtcNow;
            var fromUtc = nowUtc.AddDays(-days);
            var search = NormalizeSearch(q ?? query);

            var dbQuery = db.PageViews.AsNoTracking()
                .Where(x => x.CreatedAt >= fromUtc && x.CreatedAt <= nowUtc);

            if (userId.HasValue) dbQuery = dbQuery.Where(x => x.UserId == userId);

            var rows = await dbQuery.OrderByDescending(x => x.CreatedAt).ToListAsync(ct);
            var users = await LoadUserSummariesAsync(rows.Select(x => x.UserId).Where(x => x.HasValue).Select(x => x!.Value), cfg, httpFactory, ct);

            var items = rows.Select(x => ToActivityItem(x, x.UserId.HasValue ? users.GetValueOrDefault(x.UserId.Value) : null)).ToList();

            if (!string.IsNullOrWhiteSpace(category)) items = items.Where(x => string.Equals(x.Category, category, StringComparison.OrdinalIgnoreCase)).ToList();
            if (!string.IsNullOrWhiteSpace(actionType)) items = items.Where(x => string.Equals(x.ActionType, actionType, StringComparison.OrdinalIgnoreCase)).ToList();
            if (!string.IsNullOrWhiteSpace(source)) items = items.Where(x => string.Equals(x.Source, source, StringComparison.OrdinalIgnoreCase)).ToList();
            if (!string.IsNullOrWhiteSpace(search))
            {
                items = items.Where(x => NormalizeSearch($"{x.User?.FullName} {x.User?.Email} {x.Category} {x.ActionType} {x.Source} {x.Method} {x.Path} {x.Target} {x.Description} {x.StatusCode}").Contains(search)).ToList();
            }

            var total = items.Count;
            var pageItems = items.Skip((page - 1) * pageSize).Take(pageSize).ToList();

            var topCategories = items.GroupBy(x => x.Category)
                .Select(g => new { label = g.Key, value = g.Count() })
                .OrderByDescending(x => x.value)
                .Take(10)
                .ToList();

            var topActions = items.GroupBy(x => x.ActionType)
                .Select(g => new { label = g.Key, value = g.Count() })
                .OrderByDescending(x => x.value)
                .Take(10)
                .ToList();

            var topUsers = items.Where(x => x.User != null)
                .GroupBy(x => new { x.User!.Id, x.User.FullName, x.User.Email, x.User.Role })
                .Select(g => new { userId = g.Key.Id, fullName = g.Key.FullName, displayName = g.Key.FullName, email = g.Key.Email, role = g.Key.Role, value = g.Count() })
                .OrderByDescending(x => x.value)
                .Take(10)
                .ToList();

            return Results.Ok(new
            {
                period = new { days, fromUtc, toUtc = nowUtc },
                paging = new { page, pageSize, total },
                totals = new
                {
                    totalActions = total,
                    uniqueUsers = items.Where(x => x.UserId.HasValue).Select(x => x.UserId!.Value).Distinct().Count(),
                    errors = items.Count(x => x.StatusCode.HasValue && x.StatusCode.Value >= 400),
                    navigations = items.Count(x => string.Equals(x.Category, "navigation", StringComparison.OrdinalIgnoreCase)),
                },
                filters = new { q, query, category, actionType, source, userId },
                topCategories,
                topActions,
                topUsers,
                items = pageItems,
            });
        });

        return app;
    }
}
