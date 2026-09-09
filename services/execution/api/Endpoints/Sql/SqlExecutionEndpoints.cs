using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TaskForge.Execution.Api.Data;
using TaskForge.Execution.Api.Domain;
using TaskForge.Execution.Api.Services.Sql;
using TaskForge.Sql;

namespace TaskForge.Execution.Api.Endpoints;

internal static partial class ExecutionApiEndpoints
{
    private static void MapSqlExecutionEndpoints(WebApplication app)
    {
        var group = app.MapGroup("/api/internal/execution/sql-jobs");
        group.AddEndpointFilter(async (context, next) =>
        {
            try { return await next(context); }
            catch (ArgumentException ex) { return Microsoft.AspNetCore.Http.Results.BadRequest(new { code = "SQL_JOB_INVALID", message = ex.Message }); }
            catch (JsonException ex) { return Microsoft.AspNetCore.Http.Results.BadRequest(new { code = "SQL_JOB_INVALID", message = ex.Message }); }
        });
        group.MapPost("", async (SqlCreateJob request, ExecutionDbContext db, SqlWakeups wakeups, IConfiguration cfg, CancellationToken ct) =>
        {
            SqlQueueService.Validate(request);
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(704638912)", ct);
            var existing = await db.ExecutionJobs.AsNoTracking().FirstOrDefaultAsync(x => x.DeduplicationKey == request.DeduplicationKey, ct);
            if (existing is not null)
            {
                var old = JsonSerializer.Deserialize<SqlJobPayload>(existing.PayloadJson!, SqlWire.Json)!;
                if (existing.Kind != request.Kind || existing.UserId != request.UserId || existing.SubmissionId != request.SubmissionId
                    || old.SpecVersionId != request.Payload.SpecVersionId || old.Profile.Id != request.Payload.Profile.Id
                    || old.Source != request.Payload.Source || old.ValidationRunId != request.Payload.ValidationRunId)
                    return Microsoft.AspNetCore.Http.Results.Conflict(new { code = "SQL_REQUEST_ID_REUSED", message = "Request id is already bound to different SQL input." });
                return Microsoft.AspNetCore.Http.Results.Ok(SqlQueueService.View(existing));
            }
            var total = await db.ExecutionJobs.CountAsync(x => x.Kind.StartsWith("sql-") && (x.Status == "queued" || x.Status == "running"), ct);
            if (total >= Math.Clamp(cfg.GetValue("Sql:MaxQueuedJobs", 512), 16, 5000))
                return Microsoft.AspNetCore.Http.Results.Json(new { code = "SQL_QUEUE_BUSY", message = "SQL execution queue is full." }, statusCode: 429);
            if (request.UserId.HasValue && await db.ExecutionJobs.CountAsync(x => x.Kind.StartsWith("sql-") && x.UserId == request.UserId && (x.Status == "queued" || x.Status == "running"), ct) >= 8)
                return Microsoft.AspNetCore.Http.Results.Json(new { code = "SQL_USER_BUSY", message = "Too many pending SQL attempts for this user." }, statusCode: 429);
            var job = new ExecutionJob
            {
                Kind = request.Kind, Target = request.Payload.Profile.Fingerprint, PayloadVersion = SqlWire.Version,
                PayloadJson = SqlWire.Serialize(request.Payload), DeduplicationKey = request.DeduplicationKey,
                SubmissionId = request.SubmissionId, AssignmentId = request.Payload.AssignmentId, UserId = request.UserId,
                Code = string.Empty, Language = string.Empty, Status = "queued"
            };
            db.ExecutionJobs.Add(job); await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
            wakeups.Signal();
            return Microsoft.AspNetCore.Http.Results.Ok(SqlQueueService.View(job));
        });
        group.MapGet("/{jobId:guid}", async (Guid jobId, ExecutionDbContext db, CancellationToken ct) =>
        {
            var job = await db.ExecutionJobs.AsNoTracking().FirstOrDefaultAsync(x => x.Id == jobId && x.Kind.StartsWith("sql-"), ct);
            if (job is null) return Microsoft.AspNetCore.Http.Results.NotFound();
            var result = await db.ExecutionResults.AsNoTracking().FirstOrDefaultAsync(x => x.JobId == jobId, ct);
            return Microsoft.AspNetCore.Http.Results.Ok(SqlQueueService.View(job, result));
        });
        group.MapPost("/claim-next", async (SqlClaim request, ExecutionDbContext db, SqlWorkerDirectory workers, CancellationToken ct) =>
        {
            ValidateClaim(request);
            workers.Update(new SqlCapabilities(request.WorkerId, request.Targets));
            for (var attempt = 0; attempt < 16; attempt++)
            {
                var now = DateTimeOffset.UtcNow;
                var id = await db.ExecutionJobs.AsNoTracking().Where(x => x.Status == "queued" && x.AttemptCount < 3
                    && request.Kinds.Contains(x.Kind) && x.Target != null && request.Targets.Contains(x.Target))
                    .OrderBy(x => x.CreatedAt).Select(x => x.Id).FirstOrDefaultAsync(ct);
                if (id == Guid.Empty) return Microsoft.AspNetCore.Http.Results.Ok(new { job = (object?)null });
                var token = Guid.NewGuid();
                var count = await db.ExecutionJobs.Where(x => x.Id == id && x.Status == "queued").ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.Status, "running").SetProperty(x => x.StartedAt, now)
                    .SetProperty(x => x.AttemptCount, x => x.AttemptCount + 1).SetProperty(x => x.ClaimedByWorkerId, request.WorkerId)
                    .SetProperty(x => x.LeaseToken, token).SetProperty(x => x.LeaseExpiresAt, now.AddSeconds(45)), ct);
                if (count == 0) continue;
                var job = await db.ExecutionJobs.AsNoTracking().SingleAsync(x => x.Id == id, ct);
                return Microsoft.AspNetCore.Http.Results.Ok(new { job = SqlQueueService.View(job) });
            }
            return Microsoft.AspNetCore.Http.Results.Ok(new { job = (object?)null });
        });
        group.MapPost("/{jobId:guid}/renew", async (Guid jobId, SqlLease request, ExecutionDbContext db, CancellationToken ct) =>
        {
            var now = DateTimeOffset.UtcNow;
            var updated = await db.ExecutionJobs.Where(x => x.Id == jobId && x.Kind.StartsWith("sql-") && x.Status == "running"
                && x.ClaimedByWorkerId == request.WorkerId && x.LeaseToken == request.LeaseToken && x.LeaseExpiresAt > now && x.CreatedAt > now.AddMinutes(-15))
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.LeaseExpiresAt, now.AddSeconds(45)), ct);
            return updated == 1 ? Microsoft.AspNetCore.Http.Results.Ok(new { renewed = true })
                : Microsoft.AspNetCore.Http.Results.Conflict(new { code = "SQL_LEASE_LOST" });
        });
        group.MapPost("/{jobId:guid}/complete", async (Guid jobId, SqlComplete request, ExecutionDbContext db, CancellationToken ct) =>
        {
            if (request.Verdict is not ("Validated" or "ValidationFailed" or "Previewed" or "Accepted" or "WrongAnswer" or "RuntimeError" or "TimeLimitExceeded" or "OutputLimitExceeded" or "JudgeUnavailable")
                || request.Result.ValueKind != JsonValueKind.Object || request.Result.GetRawText().Length > 3_000_000)
                throw new ArgumentException("Invalid SQL result.");
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var job = await db.ExecutionJobs.FromSqlInterpolated($"SELECT * FROM \"ExecutionJobs\" WHERE \"Id\"={jobId} FOR UPDATE").SingleOrDefaultAsync(ct);
            if (job is null || !SqlWire.IsSqlKind(job.Kind)) return Microsoft.AspNetCore.Http.Results.NotFound();
            if (job.LeaseToken != request.LeaseToken || job.ClaimedByWorkerId != request.WorkerId)
                return Microsoft.AspNetCore.Http.Results.Conflict(new { code = "SQL_LEASE_LOST" });
            if (SqlWire.IsTerminal(job.Status)) return Microsoft.AspNetCore.Http.Results.Ok(new { completed = true });
            if (job.LeaseExpiresAt <= DateTimeOffset.UtcNow) return Microsoft.AspNetCore.Http.Results.Conflict(new { code = "SQL_LEASE_LOST" });
            if (job.Kind == "sql-check" && (request.Verdict is "Validated" or "Previewed" or "ValidationFailed")) throw new ArgumentException("A check requires a graded verdict.");
            if (job.Kind == "sql-preview" && (request.Verdict is "Accepted" or "WrongAnswer" or "Validated" or "ValidationFailed")) throw new ArgumentException("A preview cannot receive a graded or materialization verdict.");
            if (job.Kind == "sql-materialize" && request.Verdict is not ("Validated" or "ValidationFailed" or "JudgeUnavailable")) throw new ArgumentException("Materialization requires a validation verdict.");
            if (request.Passed != (request.Verdict == "Accepted")) throw new ArgumentException("Verdict and passed flag disagree.");
            job.Status = request.Verdict; job.CompletedAt = DateTimeOffset.UtcNow;
            db.ExecutionResults.Add(new ExecutionResult { JobId = jobId, Status = request.Verdict, Passed = request.Passed,
                Score = request.Passed ? 100 : 0, DurationMs = Math.Clamp(request.DurationMs, 0, 900_000), ResultJson = request.Result.GetRawText() });
            await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
            return Microsoft.AspNetCore.Http.Results.Ok(new { completed = true });
        });
        app.MapPost("/api/internal/execution/sql-capabilities", (SqlCapabilities request, SqlWorkerDirectory workers) =>
        {
            if (string.IsNullOrWhiteSpace(request.WorkerId) || request.WorkerId.Length > 160 || request.Targets is null
                || request.Targets.Length > 32 || request.Targets.Any(x => !SqlWire.IsHash(x)))
                return Microsoft.AspNetCore.Http.Results.BadRequest();
            workers.Update(request); return Microsoft.AspNetCore.Http.Results.Ok(new { accepted = true });
        });
        app.MapGet("/api/internal/execution/sql-capabilities", (SqlWorkerDirectory workers, string? target) =>
            Microsoft.AspNetCore.Http.Results.Ok(new { available = target is null ? workers.View().Length > 0 : workers.Available(target), workers = workers.View() }));
        app.MapGet("/api/execution/sql-status", async (ExecutionDbContext db, SqlWorkerDirectory workers, CancellationToken ct) =>
            Microsoft.AspNetCore.Http.Results.Ok(new { workers = workers.View(),
                queued = await db.ExecutionJobs.CountAsync(x => x.Kind.StartsWith("sql-") && x.Status == "queued", ct),
                running = await db.ExecutionJobs.CountAsync(x => x.Kind.StartsWith("sql-") && x.Status == "running", ct) }));
    }

    private static void ValidateClaim(SqlClaim request)
    {
        if (string.IsNullOrWhiteSpace(request.WorkerId) || request.WorkerId.Length > 160 || request.Kinds is null
            || request.Kinds.Length is < 1 or > 3 || request.Kinds.Any(x => !SqlWire.IsSqlKind(x))
            || request.Targets is null || request.Targets.Length is < 1 or > 32 || request.Targets.Any(x => !SqlWire.IsHash(x)))
            throw new ArgumentException("SQL claims require an identified worker and exact supported targets.");
    }
}
