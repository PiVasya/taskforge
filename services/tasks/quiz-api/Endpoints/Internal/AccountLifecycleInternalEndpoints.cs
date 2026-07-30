using Microsoft.EntityFrameworkCore;
using QuizTaskService.Data;

namespace QuizTaskService.Endpoints;

internal static partial class QuizTaskEndpoints
{
    private sealed record AccountLifecycleMutationRequest(Guid OperationId, Guid SourceUserId, Guid? TargetUserId);

    private static WebApplication MapAccountLifecycleInternalEndpoints(WebApplication app)
    {
        app.MapPost("/api/internal/account-lifecycle/merge", async (
            AccountLifecycleMutationRequest request,
            QuizDbContext db,
            CancellationToken ct) =>
        {
            if (!request.TargetUserId.HasValue || request.TargetUserId == request.SourceUserId)
                return Results.BadRequest(new { message = "Некорректная пара аккаунтов.", code = "INVALID_MERGE_USERS" });

            var targetId = request.TargetUserId.Value;
            await using var transaction = await db.Database.BeginTransactionAsync(ct);

            var targetAttemptIds = await db.Attempts.AsNoTracking()
                .Where(x => x.UserId == targetId)
                .Select(x => x.ClientAttemptId)
                .ToListAsync(ct);
            var targetAttemptSet = targetAttemptIds.ToHashSet();
            var sourceAttempts = await db.Attempts.Where(x => x.UserId == request.SourceUserId).ToListAsync(ct);
            var duplicateAttempts = 0;
            foreach (var attempt in sourceAttempts)
            {
                if (!targetAttemptSet.Add(attempt.ClientAttemptId))
                {
                    db.Attempts.Remove(attempt);
                    duplicateAttempts++;
                }
                else
                {
                    attempt.UserId = targetId;
                }
            }

            var targetProgress = await db.Progress.Where(x => x.UserId == targetId).ToListAsync(ct);
            var targetProgressMap = targetProgress.ToDictionary(x => x.TaskId);
            var sourceProgress = await db.Progress.Where(x => x.UserId == request.SourceUserId).ToListAsync(ct);
            var mergedProgress = 0;
            var movedProgress = 0;
            foreach (var source in sourceProgress)
            {
                if (targetProgressMap.TryGetValue(source.TaskId, out var target))
                {
                    target.Solved |= source.Solved;
                    target.BestScore = Math.Max(target.BestScore, source.BestScore);
                    target.BestScorePercent = Math.Max(target.BestScorePercent, source.BestScorePercent);
                    target.AttemptsCount += source.AttemptsCount;
                    if (!target.FirstSolvedAt.HasValue || (source.FirstSolvedAt.HasValue && source.FirstSolvedAt < target.FirstSolvedAt))
                        target.FirstSolvedAt = source.FirstSolvedAt;
                    if (source.LastAttemptAt > target.LastAttemptAt)
                    {
                        target.LastAttemptAt = source.LastAttemptAt;
                        target.LastAttemptId = source.LastAttemptId;
                    }
                    db.Progress.Remove(source);
                    mergedProgress++;
                }
                else
                {
                    source.UserId = targetId;
                    targetProgressMap[source.TaskId] = source;
                    movedProgress++;
                }
            }

            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return Results.Ok(new
            {
                request.OperationId,
                movedAttempts = sourceAttempts.Count - duplicateAttempts,
                duplicateAttempts,
                movedProgress,
                mergedProgress,
            });
        });

        app.MapPost("/api/internal/account-lifecycle/delete", async (
            AccountLifecycleMutationRequest request,
            QuizDbContext db,
            CancellationToken ct) =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var attempts = await db.Attempts.Where(x => x.UserId == request.SourceUserId).ToListAsync(ct);
            var progress = await db.Progress.Where(x => x.UserId == request.SourceUserId).ToListAsync(ct);
            db.Attempts.RemoveRange(attempts);
            db.Progress.RemoveRange(progress);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return Results.Ok(new { request.OperationId, deletedAttempts = attempts.Count, deletedProgress = progress.Count });
        });

        return app;
    }
}
