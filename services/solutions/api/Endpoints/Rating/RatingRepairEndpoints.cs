using TaskForge.Solutions.Api.Services.Results;
using Microsoft.EntityFrameworkCore;
using TaskForge.Solutions.Api.Data;
using TaskForge.Solutions.Api.Domain;
using TaskForge.Solutions.Api.Contracts;
using static TaskForge.Solutions.Api.Services.Mapping.SolutionsApiMappingService;
using static TaskForge.Solutions.Api.Services.Results.SolutionsApiResultsService;

namespace TaskForge.Solutions.Api.Endpoints;

internal static partial class SolutionsApiEndpoints
{
    private const string RatingRebuildRequestName = "UserRatingsFullRebuildRequested";
    private const string RatingRebuildCompletionName = "UserRatingsFullRebuild";

    private static WebApplication MapRatingRepairEndpoints(WebApplication app)
    {
        // Protected by the existing /api/admin/* role gate.
        app.MapPost("/api/admin/rating/rebuild", async (SolutionsDbContext db, CancellationToken ct) =>
        {
            var generation = await QueueRatingRebuildAsync(db, ct);
            return Microsoft.AspNetCore.Http.Results.Accepted(value: new
            {
                queued = true,
                generation,
                message = "Полный пересчёт рейтинга поставлен в очередь. Рейтинг и Minecraft-баланс будут рассчитаны по действующим заданиям."
            });
        });

        app.MapGet("/api/admin/rating/rebuild", async (SolutionsDbContext db, CancellationToken ct) =>
        {
            var checkpoints = await db.RatingProjectionCheckpoints.AsNoTracking()
                .Where(x => x.ProjectionName == RatingRebuildRequestName || x.ProjectionName == RatingRebuildCompletionName
                    || x.ProjectionName == LeaderboardReadModelService.ReadyCheckpoint)
                .ToListAsync(ct);
            var requested = checkpoints.FirstOrDefault(x => x.ProjectionName == RatingRebuildRequestName);
            var completed = checkpoints.FirstOrDefault(x => x.ProjectionName == RatingRebuildCompletionName);
            var activitiesReady = checkpoints.Any(x => x.ProjectionName == LeaderboardReadModelService.ReadyCheckpoint && x.LastEventSequence == 1);
            var dirtyCount = await db.RatingDirtyUsers.CountAsync(ct);
            return Microsoft.AspNetCore.Http.Results.Ok(new
            {
                generation = requested?.LastEventSequence ?? 0,
                completedGeneration = completed?.LastEventSequence ?? 0,
                pending = !activitiesReady || (requested?.LastEventSequence ?? 0) > (completed?.LastEventSequence ?? 0),
                projectionReady = activitiesReady,
                dirtyUsers = dirtyCount,
                lastCompletedAtUtc = completed?.UpdatedAt
            });
        });

        // Read-only per-user source audit; historical solutions are retained, but
        // deleted tasks/courses are never a source of rating points.
        app.MapGet("/api/admin/rating/users/{userId:guid}/audit", async (
            Guid userId, SolutionsDbContext db, IConfiguration cfg, IHttpClientFactory clients, CancellationToken ct) =>
        {
            var acceptedCodeIds = await db.Submissions.AsNoTracking()
                .Where(x => x.UserId == userId && x.Status == "Accepted" && x.AssignmentId != Guid.Empty)
                .Select(x => x.AssignmentId).Distinct().ToListAsync(ct);
            var acceptedImageIds = await db.ImageSolutions.AsNoTracking()
                .Where(x => x.UserId == userId && x.Passed && x.AssignmentId != Guid.Empty)
                .Select(x => x.AssignmentId).Distinct().ToListAsync(ct);
            var allSolutionIds = acceptedCodeIds.Concat(acceptedImageIds).Distinct().ToArray();
            var taskRows = await LoadTaskLeaderboardRowsAsync(null, null, new[] { userId }, cfg, clients, ct);
            var allAssignmentIds = allSolutionIds.Concat(taskRows.Select(x => x.AssignmentId)).Distinct().ToArray();
            var metadata = await LoadAssignmentMetadataAsync(allAssignmentIds, cfg, clients, ct, requireRatingSources: true);

            var contributions = new List<(Guid Id, int Rating, string Kind)>();
            contributions.AddRange(acceptedCodeIds.Where(metadata.ContainsKey)
                .Select(id => (id, MetadataRating(metadata, id), "code")));
            contributions.AddRange(acceptedImageIds.Where(metadata.ContainsKey)
                .Select(id => (id, MetadataRating(metadata, id), "image")));
            contributions.AddRange(taskRows.Where(x => x.UserId == userId)
                .Select(x => (x.AssignmentId, x.Rating, x.Kind)));

            var grouped = contributions.GroupBy(x => x.Id)
                .Select(group =>
                {
                    var info = metadata.GetValueOrDefault(group.Key);
                    return new
                    {
                        assignmentId = group.Key,
                        courseId = info?.CourseId,
                        title = info?.Title ?? "Задание",
                        rating = group.Max(x => x.Rating),
                        sources = group.Select(x => x.Kind).Distinct().OrderBy(x => x).ToArray()
                    };
                })
                .OrderByDescending(x => x.rating)
                .ThenBy(x => x.assignmentId)
                .ToList();

            return Microsoft.AspNetCore.Http.Results.Ok(new
            {
                userId,
                totalScore = grouped.Sum(x => (long)x.rating),
                solvedCount = grouped.Count,
                ignoredDeletedAssignmentCount = allSolutionIds.Count(id => !metadata.ContainsKey(id)),
                topAssignments = grouped.Take(50).ToArray(),
                totalAssignments = grouped.Count
            });
        });

        // Education API calls this after deleting a course/subtree.
        app.MapPost("/api/internal/rating/rebuild", async (SolutionsDbContext db, CancellationToken ct) =>
        {
            var generation = await QueueRatingRebuildAsync(db, ct);
            return Microsoft.AspNetCore.Http.Results.Accepted(value: new { queued = true, generation });
        });
        return app;
    }

    private static async Task<long> QueueRatingRebuildAsync(SolutionsDbContext db, CancellationToken ct)
    {
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO "RatingProjectionCheckpoints" ("ProjectionName", "LastEventSequence", "UpdatedAt")
            VALUES ('UserRatingsFullRebuildRequested', 1, NOW())
            ON CONFLICT ("ProjectionName") DO UPDATE SET
                "LastEventSequence" = "RatingProjectionCheckpoints"."LastEventSequence" + 1,
                "UpdatedAt" = NOW();
            """, ct);
        return await db.RatingProjectionCheckpoints.AsNoTracking()
            .Where(x => x.ProjectionName == RatingRebuildRequestName)
            .Select(x => x.LastEventSequence).SingleAsync(ct);
    }
}
