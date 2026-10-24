using TaskForge.Solutions.Api.Services.Results;
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

            var ordered = q.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id);
            return StreamSolutionHistory(ordered, System.Math.Max(0, skip),
                System.Math.Clamp(take, 1, 200), false, IsEditor(http, cfg), cfg, httpFactory, ct);
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

            // Keep the historical all=true/period response shape, but stream
            // it page by page. Old stored verdicts can contain megabytes of test
            // output; ToListAsync() of the entire history exhausts RAM.
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
            return StreamSolutionHistory(ordered, loadAll ? 0 : System.Math.Max(0, skip),
                loadAll ? null : System.Math.Clamp(take, 1, 200), true, false, cfg, httpFactory, ct);
        });

        app.MapDelete("/api/admin/users/{userId:guid}/solutions", async (Guid userId, SolutionsDbContext db, Guid? assignmentId, int? days, string? kind, CancellationToken ct) =>
        {
            if (userId == Guid.Empty) return Microsoft.AspNetCore.Http.Results.BadRequest(new { code = "USER_ID_REQUIRED" });
            if (!IsValidSolutionHistoryKind(kind)) return Microsoft.AspNetCore.Http.Results.BadRequest(new { code = "INVALID_SOLUTION_KIND" });
            var q = FilterSolutionHistoryKind(db.Submissions.Where(x => x.UserId == userId), kind);
            if (assignmentId.HasValue) q = q.Where(x => x.AssignmentId == assignmentId.Value);
            if (days.HasValue && days.Value > 0)
            {
                var since = DateTimeOffset.UtcNow.AddDays(-days.Value);
                q = q.Where(x => x.CreatedAt >= since);
            }

            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var deleted = await q.ExecuteDeleteAsync(ct);
            if (deleted > 0)
                await MarkRatingDirtyAsync(db, userId, "solutions-bulk-deleted", assignmentId, ct);
            await transaction.CommitAsync(ct);
            return Microsoft.AspNetCore.Http.Results.Ok(new { deleted, assignmentId, days, kind, userId });
        });

        app.MapGet("/api/admin/solution-users", async (SolutionsDbContext db, IConfiguration cfg, IHttpClientFactory httpFactory, string? q, int take = 200, CancellationToken ct = default) =>
        {
            if (!await LeaderboardReadModelService.IsReadyAsync(db, ct))
                return Microsoft.AspNetCore.Http.Results.Json(new { code = "RATING_PROJECTION_BUILDING" }, statusCode: 503);

            var aggregated = await db.UserRatings.AsNoTracking()
                .Where(x => x.SolvedCount > 0)
                .OrderByDescending(x => x.TotalScore)
                .ThenByDescending(x => x.SolvedCount)
                .ThenByDescending(x => x.LastAcceptedAt)
                .ToListAsync(ct);
            var users = await LoadLeaderboardUserSummariesAsync(aggregated.Select(x => x.UserId), cfg, httpFactory, ct);
            var search = NormalizeSearch(q is { Length: > 128 } ? q[..128] : q);
            var rows = aggregated
                .Select(x => new { Row = x, User = users.GetValueOrDefault(x.UserId) })
                .Where(x => string.IsNullOrWhiteSpace(search) || UserSummarySearchScore(x.User, x.Row.UserId, search) <= System.Math.Max(1, System.Math.Min(4, search.Length / 3)) || UserSummaryHaystack(x.User, x.Row.UserId).Contains(search, StringComparison.OrdinalIgnoreCase))
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
                    score = x.Row.TotalScore,
                    rating = x.Row.TotalScore,
                    totalScore = x.Row.TotalScore,
                    solved = x.Row.SolvedCount,
                    solvedCount = x.Row.SolvedCount,
                    totalAttempts = x.Row.AttemptsCount,
                    lastSubmitAt = x.Row.LastAcceptedAt
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
