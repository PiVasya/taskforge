using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using TaskForge.Solutions.Api.Data;
using TaskForge.Solutions.Api.Domain;
using TaskForge.Realtime;

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
    private static WebApplication MapSubmissionsEndpoints(WebApplication app)
    {
        app.MapGet("/api/me/solutions", async (HttpContext http, IConfiguration cfg, SolutionsDbContext db, IHttpClientFactory httpFactory, Guid? assignmentId, int? days, string? kind, int skip = 0, int take = 50, CancellationToken ct = default) =>
        {
            if (!IsValidSolutionHistoryKind(kind)) return Microsoft.AspNetCore.Http.Results.BadRequest(new { code = "INVALID_SOLUTION_KIND" });
            var uid = CurrentUserId(http, cfg);
            if (uid == null) return Unauthorized();

            var q = FilterSolutionHistoryKind(db.Submissions.AsNoTracking().Where(x => x.UserId == uid.Value), kind);
            if (assignmentId.HasValue) q = q.Where(x => x.AssignmentId == assignmentId.Value);
            if (days.HasValue && days.Value > 0)
            {
                var since = DateTimeOffset.UtcNow.AddDays(-days.Value);
                q = q.Where(x => x.CreatedAt >= since);
            }

            var rows = await q
                .OrderByDescending(x => x.CreatedAt)
                .ThenByDescending(x => x.Id)
                .Skip(System.Math.Max(0, skip))
                .Take(System.Math.Clamp(take, 1, 200))
                .ToListAsync(ct);
            var includeHiddenDetails = IsEditor(http, cfg);
            var metadata = await LoadAssignmentMetadataAsync(rows.Select(x => x.AssignmentId), cfg, httpFactory, ct);
            return Microsoft.AspNetCore.Http.Results.Ok(rows.Select(x => ToDto(x, includeHiddenDetails, metadata.GetValueOrDefault(x.AssignmentId))).ToList());
        });

        app.MapGet("/api/me/solutions/{id:guid}", async (Guid id, HttpContext http, IConfiguration cfg, SolutionsDbContext db, IHttpClientFactory httpFactory, CancellationToken ct) =>
        {
            var uid = CurrentUserId(http, cfg);
            if (uid == null) return Unauthorized();

            var s = await db.Submissions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
            if (s == null) return Microsoft.AspNetCore.Http.Results.NotFound(new { message = "Решение не найдено.", code = "SOLUTION_NOT_FOUND" });
            if (s.UserId != uid.Value) return Microsoft.AspNetCore.Http.Results.Json(new { message = "Нет доступа к этому решению.", code = "SOLUTION_FORBIDDEN" }, statusCode: 403);
            var metadata = await LoadAssignmentMetadataAsync(new[] { s.AssignmentId }, cfg, httpFactory, ct);
            return Microsoft.AspNetCore.Http.Results.Ok(ToDto(s, includeSensitiveResult: IsEditor(http, cfg), metadata: metadata.GetValueOrDefault(s.AssignmentId)));
        });

        app.MapGet("/api/admin/solutions/{id:guid}", async (Guid id, SolutionsDbContext db, IConfiguration cfg, IHttpClientFactory httpFactory, CancellationToken ct) =>
        {
            var s = await db.Submissions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
            if (s == null) return Microsoft.AspNetCore.Http.Results.NotFound(new { message = "Решение не найдено.", code = "SOLUTION_NOT_FOUND" });
            var metadata = await LoadAssignmentMetadataAsync(new[] { s.AssignmentId }, cfg, httpFactory, ct);
            return Microsoft.AspNetCore.Http.Results.Ok(ToDto(s, includeSensitiveResult: true, metadata: metadata.GetValueOrDefault(s.AssignmentId)));
        });

        app.MapDelete("/api/admin/solutions/{id:guid}", async (Guid id, SolutionsDbContext db, CancellationToken ct) =>
        {
            var s = await db.Submissions.FindAsync([id], ct);
            if (s == null) return Microsoft.AspNetCore.Http.Results.NotFound();
            if (s.UserId.HasValue) await MarkRatingDirtyAsync(db, s.UserId.Value, "code-solution-deleted", s.AssignmentId, ct);
            db.Submissions.Remove(s);
            await db.SaveChangesAsync(ct);
            return Microsoft.AspNetCore.Http.Results.Ok(new { deleted = id });
        });

        app.MapGet("/api/admin/users/{userId:guid}/solutions", async (Guid userId, HttpContext http, SolutionsDbContext db, IConfiguration cfg, IHttpClientFactory httpFactory, Guid? assignmentId, int? days, string? kind, int skip = 0, int take = 50, bool all = false, CancellationToken ct = default) =>
        {
            if (!IsValidSolutionHistoryKind(kind)) return Microsoft.AspNetCore.Http.Results.BadRequest(new { code = "INVALID_SOLUTION_KIND" });
            var q = FilterSolutionHistoryKind(db.Submissions.AsNoTracking().Where(x => x.UserId == userId), kind);
            if (assignmentId.HasValue) q = q.Where(x => x.AssignmentId == assignmentId.Value);
            if (days.HasValue && days.Value > 0)
            {
                var since = DateTimeOffset.UtcNow.AddDays(-days.Value);
                q = q.Where(x => x.CreatedAt >= since);
            }

            var total = await q.CountAsync(ct);
            http.Response.Headers["X-Total-Count"] = total.ToString(System.Globalization.CultureInfo.InvariantCulture);
            http.Response.Headers["X-Result-User-Id"] = userId.ToString("D");

            // Restore the historical admin contract: a period request meant
            // "load the whole matching history". The microservice split lost
            // that branch and silently applied Take(50) after the date filter.
            // all=true also makes the intent explicit for the current UI.
            var loadAll = ShouldLoadAllAdminHistory(all, days, take);
            var ordered = q
                .OrderByDescending(x => x.CreatedAt)
                .ThenByDescending(x => x.Id)
                .Select(x => new SolutionSubmission
                {
                    Id = x.Id,
                    AssignmentId = x.AssignmentId,
                    UserId = x.UserId,
                    Language = x.Language,
                    Status = x.Status,
                    Score = x.Score,
                    ResultJson = x.ResultJson,
                    SqlSpecVersionId = x.SqlSpecVersionId,
                    CreatedAt = x.CreatedAt
                });
            var rows = loadAll
                ? await ordered.ToListAsync(ct)
                : await ordered
                    .Skip(System.Math.Max(0, skip))
                    .Take(System.Math.Clamp(take, 1, 200))
                    .ToListAsync(ct);

            var metadata = await LoadAssignmentMetadataAsync(rows.Select(x => x.AssignmentId), cfg, httpFactory, ct);
            return Microsoft.AspNetCore.Http.Results.Ok(rows.Select(x => ToAdminHistoryDto(x, metadata.GetValueOrDefault(x.AssignmentId))).ToList());
        });

        app.MapDelete("/api/admin/users/{userId:guid}/solutions", async (Guid userId, SolutionsDbContext db, Guid? assignmentId, int? days, string? kind) =>
        {
            if (!IsValidSolutionHistoryKind(kind)) return Microsoft.AspNetCore.Http.Results.BadRequest(new { code = "INVALID_SOLUTION_KIND" });
            var q = FilterSolutionHistoryKind(db.Submissions.Where(x => x.UserId == userId), kind);
            if (assignmentId.HasValue) q = q.Where(x => x.AssignmentId == assignmentId.Value);
            if (days.HasValue && days.Value > 0)
            {
                var since = DateTimeOffset.UtcNow.AddDays(-days.Value);
                q = q.Where(x => x.CreatedAt >= since);
            }
            var rows = await q.ToListAsync();
            db.Submissions.RemoveRange(rows);
            await db.SaveChangesAsync();
            return Microsoft.AspNetCore.Http.Results.Ok(new { deleted = rows.Count, assignmentId, days });
        });

        app.MapGet("/api/admin/solution-users", async (SolutionsDbContext db, IConfiguration cfg, IHttpClientFactory httpFactory, string? q, int take = 200, CancellationToken ct = default) =>
        {
            var allCodeRows = await db.Submissions.AsNoTracking()
                .Where(x => x.UserId.HasValue)
                .Select(x => new { UserId = x.UserId!.Value, x.AssignmentId, x.CreatedAt, x.Status })
                .ToListAsync(ct);

            var codeRows = allCodeRows
                .Where(x => string.Equals(x.Status, "Accepted", StringComparison.OrdinalIgnoreCase))
                .Select(x => new { x.UserId, x.AssignmentId, x.CreatedAt })
                .ToList();

            var allImageRows = await db.ImageSolutions.AsNoTracking()
                .Select(x => new { x.UserId, x.AssignmentId, x.CreatedAt, x.Passed })
                .ToListAsync(ct);

            var imageRows = allImageRows
                .Where(x => x.Passed)
                .Select(x => new { x.UserId, x.AssignmentId, x.CreatedAt })
                .ToList();

            var assignmentIds = codeRows.Select(x => x.AssignmentId)
                .Concat(imageRows.Select(x => x.AssignmentId))
                .Where(x => x != Guid.Empty)
                .Distinct()
                .ToArray();
            var metadata = await LoadAssignmentMetadataAsync(assignmentIds, cfg, httpFactory, ct);

            var taskRows = await LoadTaskLeaderboardRowsAsync(null, null, null, cfg, httpFactory, ct);
            var codeAttemptCounts = allCodeRows.GroupBy(x => x.UserId).ToDictionary(g => g.Key, g => g.Count());
            var imageAttemptCounts = allImageRows.GroupBy(x => x.UserId).ToDictionary(g => g.Key, g => g.Count());
            var taskAttemptCounts = taskRows.GroupBy(x => x.UserId).ToDictionary(g => g.Key, g => g.Count());

            var activityRows = new List<LeaderboardActivityRow>();
            activityRows.AddRange(codeRows.Select(x => new LeaderboardActivityRow(x.UserId, x.AssignmentId, MetadataRating(metadata, x.AssignmentId), x.CreatedAt, "code")));
            activityRows.AddRange(imageRows.Select(x => new LeaderboardActivityRow(x.UserId, x.AssignmentId, MetadataRating(metadata, x.AssignmentId), x.CreatedAt, "image")));
            activityRows.AddRange(taskRows);

            var aggregated = activityRows
                .Where(x => x.UserId != Guid.Empty && x.AssignmentId != Guid.Empty)
                .GroupBy(x => x.UserId)
                .Select(g =>
                {
                    var distinct = g.GroupBy(x => x.AssignmentId)
                        .Select(a => new { Rating = a.Max(z => z.Rating), Last = a.Max(z => z.SubmittedAt) })
                        .ToList();
                    var attempts = codeAttemptCounts.GetValueOrDefault(g.Key) + imageAttemptCounts.GetValueOrDefault(g.Key) + taskAttemptCounts.GetValueOrDefault(g.Key);
                    return new
                    {
                        UserId = g.Key,
                        SolvedAssignments = distinct.Count,
                        Score = distinct.Sum(x => x.Rating),
                        TotalAttempts = attempts,
                        LastSubmitAt = distinct.Count == 0 ? (DateTimeOffset?)null : distinct.Max(x => x.Last)
                    };
                })
                .Where(x => x.SolvedAssignments > 0)
                .ToList();

            var ids = aggregated.Select(x => x.UserId).Distinct().Take(2000).ToArray();
            var users = await LoadUserSummariesAsync(ids, cfg, httpFactory, ct);
            var search = NormalizeSearch(q);
            var rows = aggregated
                .Select(x => new { Row = x, User = users.GetValueOrDefault(x.UserId) })
                .Where(x => string.IsNullOrWhiteSpace(search) || UserSummarySearchScore(x.User, x.Row.UserId, search) <= System.Math.Max(1, System.Math.Min(4, search.Length / 3)) || UserSummaryHaystack(x.User, x.Row.UserId).Contains(search, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(x => x.Row.Score)
                .ThenByDescending(x => x.Row.SolvedAssignments)
                .ThenBy(x => UserLabel(x.User))
                .Take(System.Math.Clamp(take, 1, 2000))
                .Select(x => new
                {
                    id = x.Row.UserId,
                    userId = x.Row.UserId,
                    login = x.User?.Login,
                    email = x.User?.Email ?? x.User?.MaskedEmail,
                    maskedEmail = x.User?.MaskedEmail,
                    displayName = UserLabel(x.User),
                    fullName = UserLabel(x.User),
                    firstName = x.User?.FirstName,
                    lastName = x.User?.LastName,
                    score = x.Row.Score,
                    rating = x.Row.Score,
                    totalScore = x.Row.Score,
                    solved = x.Row.SolvedAssignments,
                    solvedCount = x.Row.SolvedAssignments,
                    totalAttempts = x.Row.TotalAttempts,
                    lastSubmitAt = x.Row.LastSubmitAt
                })
                .ToList();
            return Microsoft.AspNetCore.Http.Results.Ok(rows);
        });

        return app;
    }

    // Legacy SQL submissions might have the SQL engine profile or "sql" language,
    // but no specification version. Mirror the SQL identity test used for
    // /api/internal/users/{id}/activity-summary to keep histories consistent.
    // Filter inside IQueryable (before Count/Skip/Take and deletes).
    internal static bool IsValidSolutionHistoryKind(string? kind)
        => string.IsNullOrWhiteSpace(kind) || string.Equals(kind, "code", StringComparison.OrdinalIgnoreCase)
            || string.Equals(kind, "sql", StringComparison.OrdinalIgnoreCase);

    internal static IQueryable<SolutionSubmission> FilterSolutionHistoryKind(IQueryable<SolutionSubmission> source, string? kind)
    {
        if (string.Equals(kind, "code", StringComparison.OrdinalIgnoreCase))
            return source.Where(x => x.SqlSpecVersionId == null && x.SqlEngineProfileId == null
                && x.Language != "sql" && x.Language != "SQL" && x.Language != "Sql");
        if (string.Equals(kind, "sql", StringComparison.OrdinalIgnoreCase))
            return source.Where(x => x.SqlSpecVersionId != null || x.SqlEngineProfileId != null
                || x.Language == "sql" || x.Language == "SQL" || x.Language == "Sql");
        return source;
    }
}
