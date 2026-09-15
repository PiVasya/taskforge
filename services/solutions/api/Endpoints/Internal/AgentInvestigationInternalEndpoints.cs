using Microsoft.EntityFrameworkCore;
using TaskForge.Solutions.Api.Data;

using static TaskForge.Solutions.Api.Services.Image.SolutionsApiImageService;
using static TaskForge.Solutions.Api.Services.Mapping.SolutionsApiMappingService;

namespace TaskForge.Solutions.Api.Endpoints;

internal static partial class SolutionsApiEndpoints
{
    private static WebApplication MapAgentInvestigationInternalEndpoints(WebApplication app)
    {
        app.MapGet("/api/internal/agent/users/{userId:guid}/solutions", async (
            Guid userId,
            Guid? assignmentId,
            DateTimeOffset? fromUtc,
            DateTimeOffset? toUtc,
            int take,
            SolutionsDbContext db,
            IConfiguration cfg,
            IHttpClientFactory httpFactory,
            CancellationToken ct) =>
        {
            var to = toUtc ?? DateTimeOffset.UtcNow;
            var from = fromUtc ?? to.AddDays(-7);
            var limit = System.Math.Clamp(take <= 0 ? 200 : take, 1, 500);

            var codeQuery = db.Submissions.AsNoTracking()
                .Where(x => x.UserId == userId && x.CreatedAt >= from && x.CreatedAt <= to);
            var imageQuery = db.ImageSolutions.AsNoTracking()
                .Where(x => x.UserId == userId && x.CreatedAt >= from && x.CreatedAt <= to);
            if (assignmentId.HasValue)
            {
                codeQuery = codeQuery.Where(x => x.AssignmentId == assignmentId.Value);
                imageQuery = imageQuery.Where(x => x.AssignmentId == assignmentId.Value);
            }

            var codeRowsWithSentinel = await codeQuery.OrderByDescending(x => x.CreatedAt).Take(limit + 1).ToListAsync(ct);
            var imageRowsWithSentinel = await imageQuery.OrderByDescending(x => x.CreatedAt).Take(limit + 1).ToListAsync(ct);
            var codeTruncated = codeRowsWithSentinel.Count > limit;
            var imageTruncated = imageRowsWithSentinel.Count > limit;
            var codeRows = codeRowsWithSentinel.Take(limit).ToList();
            var imageRows = imageRowsWithSentinel.Take(limit).ToList();
            var assignmentIds = codeRows.Select(x => x.AssignmentId)
                .Concat(imageRows.Select(x => x.AssignmentId))
                .Where(x => x != Guid.Empty)
                .Distinct()
                .ToArray();
            var metadata = await LoadAssignmentMetadataAsync(assignmentIds, cfg, httpFactory, ct);

            var compact = new List<AgentSolutionIndexItem>(codeRows.Count + imageRows.Count);
            compact.AddRange(codeRows.Select(x =>
            {
                metadata.TryGetValue(x.AssignmentId, out var meta);
                return new AgentSolutionIndexItem(
                    x.Id,
                    x.SqlSpecVersionId.HasValue ? "sql" : "code",
                    x.UserId,
                    x.AssignmentId,
                    meta?.Title ?? "Задание",
                    meta?.CourseId,
                    meta?.CourseTitle,
                    x.Language,
                    x.ExecutionTarget,
                    x.Status,
                    x.Score,
                    x.CreatedAt,
                    x.Code?.Length ?? 0,
                    $"/assignment/{x.AssignmentId:D}");
            }));
            compact.AddRange(imageRows.Select(x =>
            {
                metadata.TryGetValue(x.AssignmentId, out var meta);
                return new AgentSolutionIndexItem(
                    x.Id,
                    "image",
                    x.UserId,
                    x.AssignmentId,
                    meta?.Title ?? "Задание",
                    meta?.CourseId,
                    meta?.CourseTitle,
                    x.Language,
                    null,
                    x.Passed ? "Accepted" : "Failed",
                    x.SimilarityPercent,
                    x.CreatedAt,
                    x.Code?.Length ?? 0,
                    $"/assignment/{x.AssignmentId:D}");
            }));

            var truncated = codeTruncated || imageTruncated || compact.Count > limit;
            var ordered = compact
                .OrderByDescending(x => x.SubmittedAtUtc)
                .Take(limit)
                .ToList();

            return Microsoft.AspNetCore.Http.Results.Ok(new
            {
                userId,
                assignmentId,
                fromUtc = from,
                toUtc = to,
                count = ordered.Count,
                limit,
                truncated,
                solutions = ordered
            });
        });

        app.MapGet("/api/internal/agent/solutions/{kind}/{id:guid}", async (
            string kind,
            Guid id,
            SolutionsDbContext db,
            IConfiguration cfg,
            IHttpClientFactory httpFactory,
            CancellationToken ct) =>
        {
            var normalized = (kind ?? string.Empty).Trim().ToLowerInvariant();
            if (normalized is "code" or "sql")
            {
                var row = await db.Submissions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
                if (row == null)
                    return Microsoft.AspNetCore.Http.Results.NotFound(new { message = "Решение не найдено.", code = "SOLUTION_NOT_FOUND" });
                var metadata = await LoadAssignmentMetadataAsync(new[] { row.AssignmentId }, cfg, httpFactory, ct);
                return Microsoft.AspNetCore.Http.Results.Ok(new
                {
                    kind = row.SqlSpecVersionId.HasValue ? "sql" : "code",
                    solution = ToDto(row, includeSensitiveResult: true, metadata: metadata.GetValueOrDefault(row.AssignmentId)),
                    assignmentUrl = $"/assignment/{row.AssignmentId:D}"
                });
            }

            if (normalized == "image")
            {
                var row = await db.ImageSolutions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
                if (row == null)
                    return Microsoft.AspNetCore.Http.Results.NotFound(new { message = "Решение не найдено.", code = "IMAGE_SOLUTION_NOT_FOUND" });
                return Microsoft.AspNetCore.Http.Results.Ok(new
                {
                    kind = "image",
                    solution = ImageDto(row, includeReference: true),
                    assignmentUrl = $"/assignment/{row.AssignmentId:D}"
                });
            }

            return Microsoft.AspNetCore.Http.Results.BadRequest(new { message = "Поддерживаются code, sql и image.", code = "SOLUTION_KIND_INVALID" });
        });

        return app;
    }

    private sealed record AgentSolutionIndexItem(
        Guid Id,
        string Kind,
        Guid? UserId,
        Guid AssignmentId,
        string AssignmentTitle,
        Guid? CourseId,
        string? CourseTitle,
        string? Language,
        string? ExecutionTarget,
        string? Status,
        int Score,
        DateTimeOffset SubmittedAtUtc,
        int CodeLength,
        string AssignmentUrl);
}
