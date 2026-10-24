using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using TaskForge.Solutions.Api.Data;
using TaskForge.Solutions.Api.Domain;

using TaskForge.Solutions.Api.Contracts;
using static TaskForge.Solutions.Api.Services.Access.SolutionsApiAccessService;
using static TaskForge.Solutions.Api.Services.Common.SolutionsApiCommonService;
using static TaskForge.Solutions.Api.Services.Image.SolutionsApiImageService;
using static TaskForge.Solutions.Api.Services.Mapping.SolutionsApiMappingService;
using static TaskForge.Solutions.Api.Services.Results.SolutionsApiResultsService;
using TaskForge.Solutions.Api.Services.Results;
using static TaskForge.Solutions.Api.Services.Serialization.SolutionsApiSerializationService;
using static TaskForge.Solutions.Api.Services.Testing.SolutionsApiTestingService;

namespace TaskForge.Solutions.Api.Endpoints;

internal static partial class SolutionsApiEndpoints
{
    private static WebApplication MapLeaderboardEndpoints(WebApplication app)
    {
        app.MapGet("/api/leaderboard", async (HttpContext http, IConfiguration cfg, SolutionsDbContext db, IDistributedCache cache, IHttpClientFactory httpFactory, Guid? courseId, int? days, Guid? groupId, string? q, Guid? viewId, int top = 100, int? page = null, int? pageSize = null, CancellationToken ct = default) =>
        {
            var uid = CurrentUserId(http, cfg);
            if (uid == null) return Unauthorized();
            var currentPage = System.Math.Max(1, page ?? 1);

            if (courseId.HasValue && !IsEditor(http, cfg))
            {
                var courseAccess = await LoadCourseAccessAsync(courseId.Value, uid.Value, cfg, httpFactory, ct);
                if (courseAccess?.CanView != true)
                {
                    return Microsoft.AspNetCore.Http.Results.Json(new { message = "Нет доступа к рейтингу этого курса.", code = "COURSE_FORBIDDEN" }, statusCode: 403);
                }
            }

            Guid[]? groupUserIds = null;
            if (groupId.HasValue)
            {
                groupUserIds = await LoadGroupMemberIdsAsync(groupId.Value, cfg, httpFactory, ct);
                if (!IsEditor(http, cfg) && !groupUserIds.Contains(uid.Value))
                {
                    return Microsoft.AspNetCore.Http.Results.Json(new { message = "Нет доступа к рейтингу этой группы.", code = "GROUP_FORBIDDEN" }, statusCode: 403);
                }
            }

            var courseScopeIds = await LoadCourseTreeIdsAsync(courseId, cfg, httpFactory, ct);

            if (!await LeaderboardReadModelService.IsReadyAsync(db, ct))
            {
                return Microsoft.AspNetCore.Http.Results.Json(new
                {
                    code = "RATING_PROJECTION_BUILDING",
                    message = "Рейтинг подготавливается. Повтори запрос после завершения пересчёта.",
                    retryAfterSeconds = 30
                }, statusCode: 503);
            }

            if (!IsAdmin(http, cfg))
            {
                var quotaResult = await ConsumeLeaderboardViewQuotaAsync(db, cache, cfg, uid.Value, viewId, ct);
                WriteQuotaHeaders(http.Response, quotaResult.quota);
                if (!quotaResult.consumed) return QuotaExceeded(quotaResult.quota);
            }

            var aggregated = await LeaderboardReadModelService.LoadAsync(db, courseId, days,
                groupUserIds, courseScopeIds, ct);
            if (aggregated.Count == 0)
            {
                var emptyPaged = page.HasValue || pageSize.HasValue;
                if (emptyPaged) return Microsoft.AspNetCore.Http.Results.Ok(new PagedResult<object>(Array.Empty<object>(), currentPage, System.Math.Clamp(pageSize ?? 20, 1, 50), 0, false));
                return Microsoft.AspNetCore.Http.Results.Ok(Array.Empty<object>());
            }

            var users = await LoadLeaderboardUserSummariesAsync(aggregated.Select(x => x.UserId), cfg, httpFactory, ct);
            var search = NormalizeSearch(q is { Length: > 128 } ? q[..128] : q);

            var filtered = aggregated
                .Select(x => new { Row = x, User = users.GetValueOrDefault(x.UserId) })
                .Where(x => x.User?.ShowInLeaderboard == true)
                .Where(x => string.IsNullOrWhiteSpace(search) || UserSummarySearchScore(x.User, x.Row.UserId, search) <= System.Math.Max(1, System.Math.Min(4, search.Length / 3)) || UserSummaryHaystack(x.User, x.Row.UserId).Contains(search, StringComparison.OrdinalIgnoreCase))
                .ToList();

            var requestedPagedShape = page.HasValue || pageSize.HasValue;
            var size = requestedPagedShape ? System.Math.Clamp(pageSize ?? 20, 1, 50) : System.Math.Clamp(top, 1, 200);
            var offset = requestedPagedShape ? (currentPage - 1) * size : 0;
            var total = filtered.Count;

            var pageRows = filtered.Skip(offset).Take(size).ToList();
            var badges = await LoadBadgeMapAsync(db, pageRows.Select(x => x.Row.UserId), ct);
            var visible = pageRows
                .Select((x, i) => new
                {
                    rank = offset + i + 1,
                    userId = x.Row.UserId,
                    login = x.User?.Login,
                    userName = UserLabel(x.User),
                    displayName = UserLabel(x.User),
                    email = x.User?.MaskedEmail,
                    maskedEmail = x.User?.MaskedEmail,
                    firstName = x.User?.FirstName,
                    lastName = x.User?.LastName,
                    avatarUrl = x.User?.AvatarUrl,
                    location = x.User?.Location,
                    education = x.User?.Education,
                    totalScore = x.Row.Score,
                    score = x.Row.Score,
                    solved = x.Row.SolvedAssignments,
                    solvedCount = x.Row.SolvedAssignments,
                    solvedAssignments = x.Row.SolvedAssignments,
                    totalAttempts = x.Row.TotalAttempts,
                    lastSubmitAt = x.Row.LastSubmitAt,
                    badges = badges.GetValueOrDefault(x.Row.UserId) ?? new List<object>()
                })
                .Cast<object>()
                .ToList();

            if (requestedPagedShape)
            {
                return Microsoft.AspNetCore.Http.Results.Ok(new PagedResult<object>(visible, currentPage, size, total, offset + visible.Count < total));
            }

            return Microsoft.AspNetCore.Http.Results.Ok(visible);
        });

        app.MapGet("/api/admin/leaderboard", async (SolutionsDbContext db) => Microsoft.AspNetCore.Http.Results.Ok(await db.UserRatings.AsNoTracking().OrderByDescending(x => x.TotalScore).ToListAsync()));

        return app;
    }

    private static async Task<Dictionary<Guid, UserSummaryDto>> LoadLeaderboardUserSummariesAsync(
        IEnumerable<Guid> userIds, IConfiguration cfg, IHttpClientFactory clients, CancellationToken ct)
    {
        var map = new Dictionary<Guid, UserSummaryDto>();
        foreach (var batch in userIds.Where(x => x != Guid.Empty).Distinct().Chunk(1000))
        {
            // Identity API limits summaries to 1000 IDs per call; truncation here
            // would silently drop users from the leaderboard and break pagination.
            var summaries = await PostInternalAsync<List<UserSummaryDto>>(clients, cfg,
                ServiceUrl(cfg, "IdentityApi", "http://identity-api:8080"),
                "/api/internal/users/summaries", new UserIdsRequest(batch), ct);
            if (summaries is null) throw new HttpRequestException("LEADERBOARD_IDENTITY_UNAVAILABLE");
            foreach (var summary in summaries)
            {
                summary.Normalize();
                if (summary.UserId != Guid.Empty) map[summary.UserId] = summary;
            }
        }
        return map;
    }
}
