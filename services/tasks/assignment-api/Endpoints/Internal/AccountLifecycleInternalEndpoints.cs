using Microsoft.EntityFrameworkCore;
using TaskForge.Tasks.Api.Data;
using TaskForge.Tasks.Api.Domain;

namespace TaskForge.Tasks.Api.Endpoints;

internal static partial class AssignmentApiEndpoints
{
    private sealed record AccountLifecycleMutationRequest(Guid OperationId, Guid SourceUserId, Guid? TargetUserId);

    private static WebApplication MapAccountLifecycleInternalEndpoints(WebApplication app)
    {
        app.MapPost("/api/internal/account-lifecycle/merge", async (
            AccountLifecycleMutationRequest request,
            TasksDbContext db,
            CancellationToken ct) =>
        {
            if (!request.TargetUserId.HasValue || request.TargetUserId == request.SourceUserId)
                return Results.BadRequest(new { message = "Некорректная пара аккаунтов.", code = "INVALID_MERGE_USERS" });

            var targetId = request.TargetUserId.Value;
            await using var transaction = await db.Database.BeginTransactionAsync(ct);

            var attempts = await db.Attempts.Where(x => x.UserId == request.SourceUserId).ToListAsync(ct);
            foreach (var row in attempts) row.UserId = targetId;

            var snapshots = await db.AssignmentCodeSnapshots.Where(x => x.UserId == request.SourceUserId).ToListAsync(ct);
            foreach (var row in snapshots) row.UserId = targetId;

            var targetEventKeys = await db.AssignmentActivityEvents.AsNoTracking()
                .Where(x => x.UserId == targetId && x.EventUid != null)
                .Select(x => new { x.AssignmentId, x.SessionId, x.EventUid })
                .ToListAsync(ct);
            var eventKeySet = targetEventKeys
                .Select(x => EventKey(x.AssignmentId, x.SessionId, x.EventUid))
                .ToHashSet(StringComparer.Ordinal);
            var events = await db.AssignmentActivityEvents.Where(x => x.UserId == request.SourceUserId).ToListAsync(ct);
            var duplicateEvents = 0;
            foreach (var row in events)
            {
                if (row.EventUid != null && eventKeySet.Contains(EventKey(row.AssignmentId, row.SessionId, row.EventUid)))
                {
                    db.AssignmentActivityEvents.Remove(row);
                    duplicateEvents++;
                }
                else
                {
                    row.UserId = targetId;
                }
            }

            var targetSessions = await db.AssignmentWorkSessions
                .Where(x => x.UserId == targetId)
                .ToListAsync(ct);
            var targetSessionMap = targetSessions.ToDictionary(x => SessionKey(x.AssignmentId, x.SessionId), StringComparer.Ordinal);
            var sourceSessions = await db.AssignmentWorkSessions
                .Where(x => x.UserId == request.SourceUserId)
                .ToListAsync(ct);
            var mergedSessions = 0;
            var movedSessions = 0;
            foreach (var source in sourceSessions)
            {
                var key = SessionKey(source.AssignmentId, source.SessionId);
                if (targetSessionMap.TryGetValue(key, out var target))
                {
                    MergeWorkSession(target, source);
                    db.AssignmentWorkSessions.Remove(source);
                    mergedSessions++;
                }
                else
                {
                    source.UserId = targetId;
                    targetSessionMap[key] = source;
                    movedSessions++;
                }
            }

            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return Results.Ok(new
            {
                request.OperationId,
                movedAttempts = attempts.Count,
                movedSnapshots = snapshots.Count,
                movedEvents = events.Count - duplicateEvents,
                duplicateEvents,
                movedSessions,
                mergedSessions,
            });
        });

        app.MapPost("/api/internal/account-lifecycle/delete", async (
            AccountLifecycleMutationRequest request,
            TasksDbContext db,
            CancellationToken ct) =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var attempts = await db.Attempts.Where(x => x.UserId == request.SourceUserId).ToListAsync(ct);
            var snapshots = await db.AssignmentCodeSnapshots.Where(x => x.UserId == request.SourceUserId).ToListAsync(ct);
            var events = await db.AssignmentActivityEvents.Where(x => x.UserId == request.SourceUserId).ToListAsync(ct);
            var sessions = await db.AssignmentWorkSessions.Where(x => x.UserId == request.SourceUserId).ToListAsync(ct);
            db.Attempts.RemoveRange(attempts);
            db.AssignmentCodeSnapshots.RemoveRange(snapshots);
            db.AssignmentActivityEvents.RemoveRange(events);
            db.AssignmentWorkSessions.RemoveRange(sessions);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return Results.Ok(new
            {
                request.OperationId,
                deletedAttempts = attempts.Count,
                deletedSnapshots = snapshots.Count,
                deletedEvents = events.Count,
                deletedSessions = sessions.Count,
            });
        });

        return app;
    }

    private static string EventKey(Guid assignmentId, string sessionId, string? eventUid)
        => $"{assignmentId:N}:{sessionId}:{eventUid}";

    private static string SessionKey(Guid assignmentId, string sessionId)
        => $"{assignmentId:N}:{sessionId}";

    private static void MergeWorkSession(AssignmentWorkSession target, AssignmentWorkSession source)
    {
        var sourceIsNewest = source.LastActivityAt >= target.LastActivityAt;
        target.StartedAt = target.StartedAt <= source.StartedAt ? target.StartedAt : source.StartedAt;
        target.LastActivityAt = target.LastActivityAt >= source.LastActivityAt ? target.LastActivityAt : source.LastActivityAt;
        target.FinishedAt = MaxDate(target.FinishedAt, source.FinishedAt);
        target.TotalDurationMs += source.TotalDurationMs;
        target.ActiveDurationMs += source.ActiveDurationMs;
        target.HiddenDurationMs += source.HiddenDurationMs;
        target.BlurDurationMs += source.BlurDurationMs;
        target.OpenCount += source.OpenCount;
        target.CloseCount += source.CloseCount;
        target.HiddenCount += source.HiddenCount;
        target.VisibleCount += source.VisibleCount;
        target.BlurCount += source.BlurCount;
        target.FocusCount += source.FocusCount;
        target.PasteCount += source.PasteCount;
        target.CopyCount += source.CopyCount;
        target.CutCount += source.CutCount;
        target.CodeChangeCount += source.CodeChangeCount;
        target.LanguageChangeCount += source.LanguageChangeCount;
        target.SubmitCount += source.SubmitCount;
        target.FailedSubmitCount += source.FailedSubmitCount;
        target.PassedSubmitCount += source.PassedSubmitCount;
        target.FullscreenExitCount += source.FullscreenExitCount;
        target.MaxCodeLength = Math.Max(target.MaxCodeLength, source.MaxCodeLength);
        if (sourceIsNewest)
        {
            target.FinalCodeLength = source.FinalCodeLength;
            target.FinalCodeHash = source.FinalCodeHash;
            target.LastLanguage = source.LastLanguage;
            target.LastEventType = source.LastEventType;
        }
        target.RiskScore = Math.Max(target.RiskScore, source.RiskScore);
        if (RiskRank(source.RiskLevel) > RiskRank(target.RiskLevel)) target.RiskLevel = source.RiskLevel;
        if (string.IsNullOrWhiteSpace(target.RiskReasonsJson)) target.RiskReasonsJson = source.RiskReasonsJson;
    }

    private static DateTimeOffset? MaxDate(DateTimeOffset? left, DateTimeOffset? right)
        => !left.HasValue ? right : !right.HasValue ? left : left >= right ? left : right;

    private static int RiskRank(string? value) => value?.ToLowerInvariant() switch
    {
        "critical" => 4,
        "high" => 3,
        "medium" => 2,
        _ => 1,
    };
}
