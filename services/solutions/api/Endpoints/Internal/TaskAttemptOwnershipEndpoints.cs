using System.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TaskForge.Solutions.Api.Data;
using TaskForge.Solutions.Api.Domain;
using static TaskForge.Solutions.Api.Services.Results.SolutionsApiResultsService;
using TaskForge.Solutions.Api.Services.Results;

namespace TaskForge.Solutions.Api.Endpoints;

internal static partial class SolutionsApiEndpoints
{
    internal sealed record InteractiveAttemptSearch(
        string? Kind = null, Guid? UserId = null, Guid? AssignmentId = null,
        Guid[]? AssignmentIds = null, DateTimeOffset? Since = null,
        DateTimeOffset? Until = null, bool SubmittedOnly = false,
        bool PassedOnly = false, int Skip = 0, int Take = 500,
        bool NewestFirst = true);
    internal sealed record InteractiveAttemptSearchResult(long Total, TaskAttempt[] Rows);
    internal sealed record InteractiveAttemptStart(TaskAttempt Attempt, int MaxAttempts,
        int?[]? TimeLimitsSeconds = null, bool IgnoreTimeLimit = false);
    internal sealed record InteractiveAttemptStartResult(TaskAttempt Attempt, bool Created);
    internal sealed record InteractiveAttemptChange(TaskAttempt Attempt, DateTimeOffset ExpectedUpdatedAt);
    internal sealed record InteractiveAttemptsDelete(Guid[] AssignmentIds);

    private static IQueryable<TaskAttempt> InteractiveQuery(SolutionsDbContext db, InteractiveAttemptSearch f)
    {
        var q = db.TaskAttempts.AsNoTracking().AsQueryable();
        if (f.Kind is "test" or "math") q = q.Where(x => x.Kind == f.Kind);
        if (f.UserId.HasValue) q = q.Where(x => x.UserId == f.UserId.Value);
        if (f.AssignmentId.HasValue) q = q.Where(x => x.TaskAssignmentId == f.AssignmentId.Value);
        if (f.AssignmentIds is { Length: > 0 })
        {
            var ids = f.AssignmentIds.Where(x => x != Guid.Empty).Distinct().Take(2000).ToArray();
            q = q.Where(x => ids.Contains(x.TaskAssignmentId));
        }
        if (f.Since.HasValue) q = q.Where(x => x.SubmittedAt >= f.Since.Value || (x.SubmittedAt == null && x.UpdatedAt >= f.Since.Value));
        if (f.Until.HasValue) q = q.Where(x => x.SubmittedAt <= f.Until.Value || (x.SubmittedAt == null && x.UpdatedAt <= f.Until.Value));
        if (f.SubmittedOnly) q = q.Where(x => x.SubmittedAt != null);
        if (f.PassedOnly) q = q.Where(x => x.Passed && x.SubmittedAt != null);
        return q;
    }

    private static DateTimeOffset DbNow()
    {
        // Npgsql/YSQL persist timestamptz with microsecond precision. Never
        // return a timestamp with extra .NET ticks that fails the next CAS.
        var time = DateTimeOffset.UtcNow;
        return new DateTimeOffset(time.Ticks - time.Ticks % 10, TimeSpan.Zero);
    }

    // The assignment settings use nullable slots: null/zero = no time limit.
    // If an attempt has no configured slot, it is also unlimited (do not
    // repeat the final configured limit for later attempts).
    internal static int? SelectAttemptTimeLimit(int?[]? limits, int usedAttempts) =>
        limits is not null && usedAttempts >= 0 && usedAttempts < limits.Length &&
        limits[usedAttempts] is > 0 ? limits[usedAttempts] : null;

    private static bool ValidAttemptIdentity(TaskAttempt a) =>
        a.Id != Guid.Empty && a.UserId != Guid.Empty && a.TaskAssignmentId != Guid.Empty &&
        (a.Kind is "test" or "math");

    private static bool SameAttemptIdentity(TaskAttempt existing, TaskAttempt proposed) =>
        existing.Id == proposed.Id && existing.Kind == proposed.Kind &&
        existing.UserId == proposed.UserId && existing.TaskAssignmentId == proposed.TaskAssignmentId &&
        existing.AttemptNumber == proposed.AttemptNumber;

    private static IResult AttemptConflict(string code) => Results.Conflict(new { code });

    private static async Task DirtyAsync(SolutionsDbContext db, TaskAttempt attempt, string reason, CancellationToken ct) =>
        await MarkRatingDirtyAsync(db, attempt.UserId, reason, attempt.TaskAssignmentId, ct);

    private static WebApplication MapTaskAttemptOwnershipEndpoints(WebApplication app)
    {
        app.MapGet("/api/internal/task-attempts/users/{userId:guid}/public-statistics", async (
            Guid userId, SolutionsDbContext db, CancellationToken ct) =>
        {
            if (userId == Guid.Empty) return Results.BadRequest(new { code = "USER_ID_REQUIRED" });
            return Results.Ok(await PublicTaskStatisticsService.LoadAsync(db, userId, ct));
        });

        app.MapPost("/api/internal/task-attempts/search", async (
            InteractiveAttemptSearch filter, SolutionsDbContext db, CancellationToken ct) =>
        {
            if (filter.Kind is not (null or "test" or "math") || filter.AssignmentIds?.Length > 2000 ||
                filter.Skip < 0 || filter.Take is < 1 or > 2000)
                return Results.BadRequest(new { code = "ATTEMPT_SEARCH_INVALID" });
            var q = InteractiveQuery(db, filter);
            var total = await q.LongCountAsync(ct);
            var ordered = filter.NewestFirst ? q.OrderByDescending(x => x.SubmittedAt ?? x.UpdatedAt).ThenByDescending(x => x.Id)
                : q.OrderBy(x => x.SubmittedAt ?? x.UpdatedAt).ThenBy(x => x.Id);
            var rows = await ordered.Skip(filter.Skip).Take(filter.Take).ToArrayAsync(ct);
            return Results.Ok(new InteractiveAttemptSearchResult(total, rows));
        });

        app.MapGet("/api/internal/task-attempts/{attemptId:guid}", async (Guid attemptId, SolutionsDbContext db, CancellationToken ct) =>
        {
            var attempt = await db.TaskAttempts.AsNoTracking().SingleOrDefaultAsync(x => x.Id == attemptId, ct);
            return attempt is null ? Results.NotFound(new { code = "ATTEMPT_NOT_FOUND" }) : Results.Ok(attempt);
        });

        app.MapGet("/api/internal/task-attempts/active", async (
            string kind, Guid assignmentId, Guid userId, SolutionsDbContext db, CancellationToken ct) =>
        {
            if (kind is not ("test" or "math") || assignmentId == Guid.Empty || userId == Guid.Empty)
                return Results.BadRequest(new { code = "ATTEMPT_IDENTITY_INVALID" });
            var attempt = await db.TaskAttempts.AsNoTracking().SingleOrDefaultAsync(x =>
                x.Kind == kind && x.TaskAssignmentId == assignmentId && x.UserId == userId && x.SubmittedAt == null, ct);
            return attempt is null ? Results.NotFound(new { code = "ATTEMPT_NOT_FOUND" }) : Results.Ok(attempt);
        });

        app.MapPost("/api/internal/task-attempts/start", async (
            InteractiveAttemptStart request, SolutionsDbContext db, CancellationToken ct) =>
        {
            var incoming = request.Attempt;
            if (!ValidAttemptIdentity(incoming) || incoming.SubmittedAt.HasValue || incoming.ScorePercent != 0 ||
                request.MaxAttempts < 1 ||
                request.TimeLimitsSeconds?.Length > 1000 || incoming.OrderJson?.Length > 1000000)
                return Results.BadRequest(new { code = "ATTEMPT_START_INVALID" });
            // Serialize the decision within one owner database. The partial
            // unique index forbids two concurrent active attempts for a user.
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var existing = await db.TaskAttempts.AsNoTracking().SingleOrDefaultAsync(x =>
                x.Kind == incoming.Kind && x.TaskAssignmentId == incoming.TaskAssignmentId &&
                x.UserId == incoming.UserId && x.SubmittedAt == null, ct);
            if (existing is not null) return Results.Ok(new InteractiveAttemptStartResult(existing, false));
            var used = await db.TaskAttempts.CountAsync(x =>
                x.Kind == incoming.Kind && x.TaskAssignmentId == incoming.TaskAssignmentId && x.UserId == incoming.UserId, ct);
            if (used >= request.MaxAttempts) return AttemptConflict("ATTEMPT_LIMIT_REACHED");
            incoming.AttemptNumber = used + 1;
            incoming.StartedAt = DbNow();
            incoming.CreatedAt = incoming.StartedAt;
            incoming.UpdatedAt = incoming.StartedAt;
            incoming.TimeLimitSeconds = request.IgnoreTimeLimit ? null :
                SelectAttemptTimeLimit(request.TimeLimitsSeconds, used);
            db.TaskAttempts.Add(incoming);
            try
            {
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg && pg.SqlState is "23505" or "40001")
            {
                return AttemptConflict("ATTEMPT_START_CONFLICT");
            }
            catch (PostgresException ex) when (ex.SqlState is "23505" or "40001")
            {
                return AttemptConflict("ATTEMPT_START_CONFLICT");
            }
            return Results.Ok(new InteractiveAttemptStartResult(incoming, true));
        });

        app.MapPut("/api/internal/task-attempts/{attemptId:guid}/draft", async (
            Guid attemptId, InteractiveAttemptChange request, SolutionsDbContext db, CancellationToken ct) =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var current = await db.TaskAttempts.FromSqlInterpolated(
                $"SELECT * FROM \"TaskAttempts\" WHERE \"Id\" = {attemptId} FOR UPDATE").SingleOrDefaultAsync(ct);
            if (current is null) return Results.NotFound(new { code = "ATTEMPT_NOT_FOUND" });
            if (!SameAttemptIdentity(current, request.Attempt)) return AttemptConflict("ATTEMPT_IDENTITY_IMMUTABLE");
            if (current.SubmittedAt.HasValue) return AttemptConflict("ATTEMPT_ALREADY_SUBMITTED");
            if (current.UpdatedAt != request.ExpectedUpdatedAt) return AttemptConflict("ATTEMPT_VERSION_CONFLICT");
            // A newly published test revision may restart the active timer.
            // Tasks is the trusted grader and owns that decision, but may
            // never move the start backwards or into the future.
            if (request.Attempt.StartedAt < current.StartedAt ||
                request.Attempt.StartedAt > DbNow().AddSeconds(2))
                return AttemptConflict("ATTEMPT_START_TIME_INVALID");
            var newStart = request.Attempt.StartedAt;
            current.StartedAt = new DateTimeOffset(newStart.Ticks - newStart.Ticks % 10, TimeSpan.Zero);
            current.TimeExpired = request.Attempt.TimeExpired;
            current.OrderJson = request.Attempt.OrderJson;
            current.TimeLimitSeconds = request.Attempt.TimeLimitSeconds;
            var now = DbNow();
            current.UpdatedAt = now > current.UpdatedAt ? now : current.UpdatedAt.AddTicks(10);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Results.Ok(current);
        });

        app.MapPut("/api/internal/task-attempts/{attemptId:guid}/submit", async (
            Guid attemptId, InteractiveAttemptChange request, SolutionsDbContext db, CancellationToken ct) =>
        {
            var proposed = request.Attempt;
            if (proposed.SubmittedAt is null || proposed.ScorePercent is < 0 or > 100 ||
                proposed.TotalScore < 0 || proposed.EarnedScore < 0 || proposed.EarnedScore > proposed.TotalScore ||
                proposed.TotalUnits < 0 || proposed.CorrectUnits < 0 || proposed.CorrectUnits > proposed.TotalUnits ||
                proposed.AnswersJson?.Length > 4000000 || proposed.ReviewJson?.Length > 4000000)
                return Results.BadRequest(new { code = "ATTEMPT_RESULT_INVALID" });
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var current = await db.TaskAttempts.FromSqlInterpolated(
                $"SELECT * FROM \"TaskAttempts\" WHERE \"Id\" = {attemptId} FOR UPDATE").SingleOrDefaultAsync(ct);
            if (current is null) return Results.NotFound(new { code = "ATTEMPT_NOT_FOUND" });
            if (!SameAttemptIdentity(current, proposed) || current.StartedAt != proposed.StartedAt)
                return AttemptConflict("ATTEMPT_IDENTITY_IMMUTABLE");
            // A reply may be lost after COMMIT. A retry of the identical
            // verdict must return the committed attempt, not fail or duplicate.
            if (current.SubmittedAt.HasValue)
                return current.ScorePercent == proposed.ScorePercent && current.Passed == proposed.Passed &&
                    current.TimeExpired == proposed.TimeExpired && current.TotalUnits == proposed.TotalUnits &&
                    current.CorrectUnits == proposed.CorrectUnits && current.TotalScore == proposed.TotalScore &&
                    current.EarnedScore == proposed.EarnedScore && current.AnswersJson == proposed.AnswersJson &&
                    current.ReviewJson == proposed.ReviewJson ? Results.Ok(current) :
                    AttemptConflict("ATTEMPT_ALREADY_SUBMITTED");
            if (current.UpdatedAt != request.ExpectedUpdatedAt) return AttemptConflict("ATTEMPT_VERSION_CONFLICT");
            current.OrderJson = proposed.OrderJson;
            current.TimeLimitSeconds = proposed.TimeLimitSeconds;
            current.TimeExpired = proposed.TimeExpired;
            current.SubmittedAt = DbNow();
            current.ScorePercent = proposed.ScorePercent;
            current.Passed = proposed.Passed;
            current.TotalUnits = proposed.TotalUnits;
            current.CorrectUnits = proposed.CorrectUnits;
            current.TotalScore = proposed.TotalScore;
            current.EarnedScore = proposed.EarnedScore;
            current.AnswersJson = proposed.AnswersJson;
            current.ReviewJson = proposed.ReviewJson;
            current.UpdatedAt = current.SubmittedAt.Value > current.UpdatedAt
                ? current.SubmittedAt.Value : current.UpdatedAt.AddTicks(10);
            await db.SaveChangesAsync(ct);
            await DirtyAsync(db, current, "interactive-attempt-submitted", ct);
            await tx.CommitAsync(ct);
            return Results.Ok(current);
        });

        app.MapDelete("/api/internal/task-attempts/{attemptId:guid}", async (
            Guid attemptId, SolutionsDbContext db, CancellationToken ct) =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var row = await db.TaskAttempts.SingleOrDefaultAsync(x => x.Id == attemptId, ct);
            if (row is null) return Results.NotFound(new { code = "ATTEMPT_NOT_FOUND" });
            db.TaskAttempts.Remove(row);
            await db.SaveChangesAsync(ct);
            await DirtyAsync(db, row, "interactive-attempt-deleted", ct);
            await tx.CommitAsync(ct);
            return Results.Ok(new { deleted = 1, userIds = new[] { row.UserId } });
        });

        app.MapDelete("/api/internal/task-attempts/users/{userId:guid}", async (
            Guid userId, string? kind, SolutionsDbContext db, CancellationToken ct) =>
        {
            if (userId == Guid.Empty || (kind != null && kind is not ("test" or "math")))
                return Results.BadRequest(new { code = "ATTEMPT_FILTER_INVALID" });
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var q = db.TaskAttempts.Where(x => x.UserId == userId);
            if (kind is not null) q = q.Where(x => x.Kind == kind);
            var deleted = 0;
            while (true)
            {
                var rows = await q.OrderBy(x => x.Id).Take(200).ToListAsync(ct);
                if (rows.Count == 0) break;
                db.TaskAttempts.RemoveRange(rows);
                await db.SaveChangesAsync(ct);
                db.ChangeTracker.Clear();
                deleted += rows.Count;
            }
            if (deleted > 0) await MarkRatingDirtyAsync(db, userId, "interactive-attempts-deleted", null, ct);
            await tx.CommitAsync(ct);
            return Results.Ok(new { deleted, userId, kind });
        });

        app.MapPost("/api/internal/task-attempts/delete-assignments", async (
            InteractiveAttemptsDelete request, SolutionsDbContext db, CancellationToken ct) =>
        {
            if (request.AssignmentIds is null || request.AssignmentIds.Length is < 1 or > 2000 ||
                request.AssignmentIds.Contains(Guid.Empty)) return Results.BadRequest(new { code = "ASSIGNMENTS_INVALID" });
            var ids = request.AssignmentIds.Distinct().ToArray();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var affectedUsers = new HashSet<Guid>();
            var deleted = 0;
            while (true)
            {
                var batch = await db.TaskAttempts.Where(x => ids.Contains(x.TaskAssignmentId))
                    .OrderBy(x => x.Id).Take(200).ToListAsync(ct);
                if (batch.Count == 0) break;
                foreach (var row in batch) affectedUsers.Add(row.UserId);
                db.TaskAttempts.RemoveRange(batch);
                await db.SaveChangesAsync(ct);
                db.ChangeTracker.Clear();
                deleted += batch.Count;
            }
            foreach (var userId in affectedUsers)
                await MarkRatingDirtyAsync(db, userId, "assignment-removed", null, ct);
            await tx.CommitAsync(ct);
            return Results.Ok(new { deleted, userIds = affectedUsers.ToArray() });
        });

        return app;
    }
}
