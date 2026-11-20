using Microsoft.EntityFrameworkCore;
using TaskForge.Solutions.Api.Data;
using TaskForge.Solutions.Api.Domain;
using TaskForge.Solutions.Api.Services.Results;

namespace TaskForge.Solutions.Api.Endpoints;

internal static partial class SolutionsApiEndpoints
{
    private sealed record AccountLifecycleMutationRequest(Guid OperationId, Guid SourceUserId, Guid? TargetUserId);

    private static WebApplication MapAccountLifecycleInternalEndpoints(WebApplication app)
    {
        app.MapPost("/api/internal/account-lifecycle/merge", async (
            AccountLifecycleMutationRequest request,
            SolutionsDbContext db,
            CancellationToken ct) =>
        {
            if (!request.TargetUserId.HasValue || request.SourceUserId == Guid.Empty ||
                request.TargetUserId == Guid.Empty || request.TargetUserId == request.SourceUserId)
                return Results.BadRequest(new { message = "Некорректная пара аккаунтов.", code = "INVALID_MERGE_USERS" });

            var targetId = request.TargetUserId.Value;
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            // The event ledger is owned by this service and must follow
            // account merges, not refer to a deleted source account.
            await db.AdminSolutionFeedEvents.Where(x => x.UserId == request.SourceUserId)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.UserId, targetId), ct);

            var submissions = await db.Submissions.Where(x => x.UserId == request.SourceUserId).ToListAsync(ct);
            foreach (var row in submissions) row.UserId = targetId;

            var images = await db.ImageSolutions.Where(x => x.UserId == request.SourceUserId).ToListAsync(ct);
            foreach (var row in images) row.UserId = targetId;
            var interactive = await db.TaskAttempts.Where(x => x.UserId == request.SourceUserId).ToListAsync(ct);
            var targetActive = (await db.TaskAttempts.AsNoTracking()
                .Where(x => x.UserId == targetId && x.SubmittedAt == null)
                .Select(x => new { x.Kind, x.TaskAssignmentId }).ToListAsync(ct))
                .Select(x => (x.Kind, x.TaskAssignmentId)).ToHashSet();
            foreach (var row in interactive)
            {
                // Two accounts may both be taking the same test. The unique
                // active-attempt index requires terminating the source session
                // while retaining its full history when accounts are merged.
                if (row.SubmittedAt is null && targetActive.Contains((row.Kind, row.TaskAssignmentId)))
                {
                    row.TimeExpired = true;
                    row.SubmittedAt = DateTimeOffset.UtcNow;
                    row.ScorePercent = 0;
                    row.Passed = false;
                }
                row.UserId = targetId;
                row.UpdatedAt = DateTimeOffset.UtcNow;
            }

            var targetBadgeIds = await db.UserBadges.AsNoTracking().Where(x => x.UserId == targetId).Select(x => x.BadgeId).ToListAsync(ct);
            var badgeSet = targetBadgeIds.ToHashSet();
            var sourceBadges = await db.UserBadges.Where(x => x.UserId == request.SourceUserId).ToListAsync(ct);
            var duplicateBadges = 0;
            foreach (var badge in sourceBadges)
            {
                if (badgeSet.Add(badge.BadgeId)) badge.UserId = targetId;
                else
                {
                    db.UserBadges.Remove(badge);
                    duplicateBadges++;
                }
            }

            var targetQuotas = await db.UserQuotaBuckets.Where(x => x.UserId == targetId).ToListAsync(ct);
            var quotaMap = targetQuotas.ToDictionary(x => x.BucketType, StringComparer.OrdinalIgnoreCase);
            var sourceQuotas = await db.UserQuotaBuckets.Where(x => x.UserId == request.SourceUserId).ToListAsync(ct);
            var mergedQuotas = 0;
            foreach (var quota in sourceQuotas)
            {
                if (quotaMap.TryGetValue(quota.BucketType, out var targetQuota))
                {
                    targetQuota.Tokens = Math.Max(targetQuota.Tokens, quota.Tokens);
                    targetQuota.LastRefillAtUtc = targetQuota.LastRefillAtUtc >= quota.LastRefillAtUtc ? targetQuota.LastRefillAtUtc : quota.LastRefillAtUtc;
                    targetQuota.UpdatedAtUtc = DateTimeOffset.UtcNow;
                    db.UserQuotaBuckets.Remove(quota);
                    mergedQuotas++;
                }
                else
                {
                    quota.UserId = targetId;
                    quotaMap[quota.BucketType] = quota;
                }
            }

            await db.RatingAcceptedActivities.Where(x => x.UserId == request.SourceUserId).ExecuteDeleteAsync(ct);
            var sourceRating = await db.UserRatings.FirstOrDefaultAsync(x => x.UserId == request.SourceUserId, ct);
            if (sourceRating != null) db.UserRatings.Remove(sourceRating);
            var sourceDirty = await db.RatingDirtyUsers.FirstOrDefaultAsync(x => x.UserId == request.SourceUserId, ct);
            if (sourceDirty != null) db.RatingDirtyUsers.Remove(sourceDirty);
            var targetDirty = await db.RatingDirtyUsers.FirstOrDefaultAsync(x => x.UserId == targetId, ct);
            if (targetDirty == null)
            {
                db.RatingDirtyUsers.Add(new RatingDirtyUser
                {
                    UserId = targetId,
                    Reason = "account-merge",
                    MarkedAtUtc = DateTimeOffset.UtcNow,
                });
            }
            else
            {
                targetDirty.Reason = "account-merge";
                targetDirty.MarkedAtUtc = DateTimeOffset.UtcNow;
            }

            var sourceLeaderboard = await db.LeaderboardEntries.Where(x => x.UserId == request.SourceUserId).ToListAsync(ct);
            db.LeaderboardEntries.RemoveRange(sourceLeaderboard);

            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return Results.Ok(new
            {
                request.OperationId,
                movedSubmissions = submissions.Count,
                movedImageSolutions = images.Count,
                movedInteractiveAttempts = interactive.Count,
                movedBadges = sourceBadges.Count - duplicateBadges,
                duplicateBadges,
                mergedQuotas,
                ratingScheduledForRebuild = true,
                removedLeaderboardEntries = sourceLeaderboard.Count,
            });
        });

        app.MapPost("/api/internal/account-lifecycle/delete", async (
            AccountLifecycleMutationRequest request,
            SolutionsDbContext db,
            CancellationToken ct) =>
        {
            if (request.SourceUserId == Guid.Empty)
                return Results.BadRequest(new { code = "SOURCE_USER_ID_REQUIRED" });
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            // A removed account must not persist in the administrative feed.
            await db.AdminSolutionFeedEvents.Where(x => x.UserId == request.SourceUserId)
                .ExecuteDeleteAsync(ct);
            var submissions = await db.Submissions.Where(x => x.UserId == request.SourceUserId).ToListAsync(ct);
            var images = await db.ImageSolutions.Where(x => x.UserId == request.SourceUserId).ToListAsync(ct);
            var interactive = await db.TaskAttempts.Where(x => x.UserId == request.SourceUserId).ToListAsync(ct);
            // Deleting the source rows must also deliver durable deletion
            // markers, including attempts that never reached Tasks. A removed
            // account must not be retained inside these transport events.
            db.AdminSolutionFeedEvents.AddRange(
                submissions.Select(OwnershipDeletionEvents.ForAccountDeletedSubmission));
            db.AdminSolutionFeedEvents.AddRange(
                images.Select(OwnershipDeletionEvents.ForAccountDeletedImage));
            var badges = await db.UserBadges.Where(x => x.UserId == request.SourceUserId).ToListAsync(ct);
            var quotas = await db.UserQuotaBuckets.Where(x => x.UserId == request.SourceUserId).ToListAsync(ct);
            await db.RatingAcceptedActivities.Where(x => x.UserId == request.SourceUserId).ExecuteDeleteAsync(ct);
            var ratings = await db.UserRatings.Where(x => x.UserId == request.SourceUserId).ToListAsync(ct);
            var dirty = await db.RatingDirtyUsers.Where(x => x.UserId == request.SourceUserId).ToListAsync(ct);
            var leaderboard = await db.LeaderboardEntries.Where(x => x.UserId == request.SourceUserId).ToListAsync(ct);
            db.Submissions.RemoveRange(submissions);
            db.ImageSolutions.RemoveRange(images);
            db.TaskAttempts.RemoveRange(interactive);
            db.UserBadges.RemoveRange(badges);
            db.UserQuotaBuckets.RemoveRange(quotas);
            db.UserRatings.RemoveRange(ratings);
            db.RatingDirtyUsers.RemoveRange(dirty);
            db.LeaderboardEntries.RemoveRange(leaderboard);
            await db.SaveChangesAsync(ct);
            // SaveChanges emits Deleted feed entries for interactive rows.
            // Purge their user identity in the SAME transaction as account
            // deletion; only anonymized code/image tombstones may remain.
            await db.AdminSolutionFeedEvents.Where(x => x.UserId == request.SourceUserId)
                .ExecuteDeleteAsync(ct);
            await transaction.CommitAsync(ct);
            return Results.Ok(new
            {
                request.OperationId,
                deletedSubmissions = submissions.Count,
                deletedImageSolutions = images.Count,
                deletedInteractiveAttempts = interactive.Count,
                deletedBadges = badges.Count,
                deletedQuotaBuckets = quotas.Count,
                deletedRatings = ratings.Count,
                deletedLeaderboardEntries = leaderboard.Count,
            });
        });

        return app;
    }
}
