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

            var quotaResult = await ConsumeLeaderboardViewQuotaAsync(db, cache, cfg, uid.Value, viewId, ct);
            WriteQuotaHeaders(http.Response, quotaResult.quota);
            if (!quotaResult.consumed) return QuotaExceeded(quotaResult.quota);

            var since = days.HasValue && days.Value > 0 ? DateTimeOffset.UtcNow.AddDays(-days.Value) : (DateTimeOffset?)null;
            var codeRows = await db.Submissions.AsNoTracking()
                .Where(x => x.UserId.HasValue && x.Status == "Accepted")
                .Where(x => !since.HasValue || x.CreatedAt >= since.Value)
                .ToListAsync(ct);
            var imageRows = await db.ImageSolutions.AsNoTracking()
                .Where(x => x.Passed)
                .Where(x => !since.HasValue || x.CreatedAt >= since.Value)
                .ToListAsync(ct);

            if (groupUserIds is { Length: > 0 })
            {
                var members = groupUserIds.ToHashSet();
                codeRows = codeRows.Where(x => x.UserId.HasValue && members.Contains(x.UserId.Value)).ToList();
                imageRows = imageRows.Where(x => members.Contains(x.UserId)).ToList();
            }
            else if (groupId.HasValue)
            {
                codeRows = new List<SolutionSubmission>();
                imageRows = new List<UserImageTaskSolution>();
            }

            var assignmentIds = codeRows.Select(x => x.AssignmentId).Concat(imageRows.Select(x => x.AssignmentId)).Distinct().ToArray();
            var metadata = await LoadAssignmentMetadataAsync(assignmentIds, cfg, httpFactory, ct);

            codeRows = codeRows
                .Where(x => metadata.TryGetValue(x.AssignmentId, out var m) && (courseScopeIds == null || courseScopeIds.Contains(m.CourseId)))
                .ToList();
            imageRows = imageRows
                .Where(x => metadata.TryGetValue(x.AssignmentId, out var m) && (courseScopeIds == null || courseScopeIds.Contains(m.CourseId)))
                .ToList();

            var activityRows = new List<LeaderboardActivityRow>();
            activityRows.AddRange(codeRows.Where(x => x.UserId.HasValue).Select(x => new LeaderboardActivityRow(x.UserId.Value, x.AssignmentId, MetadataRating(metadata, x.AssignmentId), x.CreatedAt, "code")));
            activityRows.AddRange(imageRows.Select(x => new LeaderboardActivityRow(x.UserId, x.AssignmentId, MetadataRating(metadata, x.AssignmentId), x.CreatedAt, "image")));
            if (!groupId.HasValue || groupUserIds is { Length: > 0 })
            {
                activityRows.AddRange(await LoadTaskLeaderboardRowsAsync(courseId, days, groupUserIds, cfg, httpFactory, ct, courseScopeIds));
            }

            if (activityRows.Count == 0)
            {
                var requestedEmptyPagedShape = page.HasValue || pageSize.HasValue;
                if (requestedEmptyPagedShape) return Microsoft.AspNetCore.Http.Results.Ok(new PagedResult<object>(Array.Empty<object>(), System.Math.Max(1, page ?? 1), System.Math.Clamp(pageSize ?? 20, 1, 50), 0, false));
                return Microsoft.AspNetCore.Http.Results.Ok(Array.Empty<object>());
            }

            var aggregated = activityRows
                .GroupBy(x => x.UserId)
                .Select(g =>
                {
                    var distinct = g.GroupBy(x => x.AssignmentId).Select(a => new { Rating = a.Max(z => z.Rating), Last = a.Max(z => z.SubmittedAt) }).ToList();
                    return new
                    {
                        UserId = g.Key,
                        SolvedAssignments = distinct.Count,
                        Score = distinct.Sum(x => x.Rating),
                        TotalAttempts = g.Count(),
                        LastSubmitAt = distinct.Max(x => x.Last)
                    };
                })
                .Where(x => x.SolvedAssignments > 0)
                .OrderByDescending(x => x.Score)
                .ThenByDescending(x => x.SolvedAssignments)
                .ThenByDescending(x => x.LastSubmitAt)
                .ToList();

            var users = await LoadUserSummariesAsync(aggregated.Select(x => x.UserId), cfg, httpFactory, ct);
            var badges = await LoadBadgeMapAsync(db, aggregated.Select(x => x.UserId), ct);
            var search = NormalizeSearch(q);

            var filtered = aggregated
                .Select(x => new { Row = x, User = users.GetValueOrDefault(x.UserId) })
                .Where(x => x.User == null || x.User.ShowInLeaderboard)
                .Where(x => string.IsNullOrWhiteSpace(search) || UserSummarySearchScore(x.User, x.Row.UserId, search) <= System.Math.Max(1, System.Math.Min(4, search.Length / 3)) || UserSummaryHaystack(x.User, x.Row.UserId).Contains(search, StringComparison.OrdinalIgnoreCase))
                .ToList();

            var requestedPagedShape = page.HasValue || pageSize.HasValue;
            var size = requestedPagedShape ? System.Math.Clamp(pageSize ?? 20, 1, 50) : System.Math.Clamp(top, 1, 200);
            var offset = requestedPagedShape ? (currentPage - 1) * size : 0;
            var total = filtered.Count;

            var visible = filtered
                .Skip(offset)
                .Take(size)
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
}
