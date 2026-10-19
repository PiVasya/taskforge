using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TaskForge.Execution.Api.Data;
using TaskForge.Execution.Api.Domain;
using TaskForge.Execution.Api.Services.Legacy;

using TaskForge.Execution.Api.Contracts;
using static TaskForge.Execution.Api.Services.Mapping.ExecutionApiMappingService;
using static TaskForge.Execution.Api.Services.Results.ExecutionApiResultsService;
using static TaskForge.Execution.Api.Services.Serialization.ExecutionApiSerializationService;

namespace TaskForge.Execution.Api.Endpoints;

internal static partial class ExecutionApiEndpoints
{
    private static WebApplication MapInternalExecutionEndpoints(WebApplication app)
    {
        app.MapPost("/api/internal/execution/compile-run", async (RunnerRequest request, IHttpClientFactory factory, IConfiguration cfg) => await ProxyRunAsync(request, factory, cfg, tests: false));

        app.MapPost("/api/internal/execution/run-tests", async (RunnerRequest request, IHttpClientFactory factory, IConfiguration cfg) => await ProxyRunAsync(request, factory, cfg, tests: true));

        app.MapPost("/api/internal/execution/jobs", async (CreateExecutionJobRequest request, ExecutionDbContext db, ILoggerFactory logs, CancellationToken ct) =>
        {
            if (request.SubmissionId == Guid.Empty) return Microsoft.AspNetCore.Http.Results.BadRequest(new { message = "submissionId is required", code = "SUBMISSION_ID_REQUIRED" });
            if (string.IsNullOrWhiteSpace(request.Code)) return Microsoft.AspNetCore.Http.Results.BadRequest(new { message = "code is required", code = "CODE_REQUIRED" });

            var job = new ExecutionJob
            {
                SubmissionId = request.SubmissionId,
                AssignmentId = request.AssignmentId,
                UserId = request.UserId,
                Language = NormalizeLanguage(request.Language),
                Code = request.Code ?? string.Empty,
                Input = request.Input,
                TestsJson = request.Tests.HasValue ? request.Tests.Value.GetRawText() : request.TestCases.HasValue ? request.TestCases.Value.GetRawText() : request.TestsJson,
                CodeForbiddenCallsJson = request.CodeForbiddenCalls.HasValue ? request.CodeForbiddenCalls.Value.GetRawText() : StringArrayJson(request.PolicyForbiddenCalls),
                CodeRequiredCallsJson = request.CodeRequiredCalls.HasValue ? request.CodeRequiredCalls.Value.GetRawText() : StringArrayJson(request.PolicyRequiredCalls),
                TimeLimitMs = request.TimeLimitMs,
                MemoryLimitMb = request.MemoryLimitMb,
                Status = "queued"
            };
            db.ExecutionJobs.Add(job);
            await db.SaveChangesAsync(ct);
            logs.CreateLogger("TaskForge.CodeQueue").LogInformation(
                "[TFQUEUE] CREATED api={ApiInstance} job={JobId} submission={SubmissionId} assignment={AssignmentId} language={Language} queuedAt={QueuedAt:O} attempts={Attempts} testsConfigured={TestsConfigured}",
                CodeQueueTrace.ApiInstance, job.Id, job.SubmissionId, job.AssignmentId, job.Language,
                job.CreatedAt, job.AttemptCount, !string.IsNullOrWhiteSpace(job.TestsJson));
            return Microsoft.AspNetCore.Http.Results.Ok(new { id = job.Id, status = job.Status });
        });

        app.MapPost("/api/internal/execution/jobs/claim-next", async (ExecutionDbContext db, IConfiguration cfg, HttpContext http, ILoggerFactory logs, CancellationToken ct) =>
        {
            var worker = CodeQueueTrace.FromRequest(http);
            var logger = logs.CreateLogger("TaskForge.CodeQueue");
            var now = DateTimeOffset.UtcNow;
            var maxAttempts = System.Math.Clamp(cfg.GetValue("ExecutionQueue:MaxAttempts", 3), 1, 10);
            var runningTimeoutMinutes = System.Math.Clamp(cfg.GetValue("ExecutionQueue:RunningTimeoutMinutes", 5), 1, 60);
            var staleBefore = now.AddMinutes(-runningTimeoutMinutes);

            var timedOut = await db.ExecutionJobs
                .Where(x => x.Kind == ExecutionJobKinds.Legacy && x.Status == "running" && x.CompletedAt == null &&
                    ((x.StartedAt != null && x.StartedAt < staleBefore) ||
                     (x.StartedAt == null && x.CreatedAt < staleBefore)) &&
                    x.AttemptCount < maxAttempts)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.Status, "queued")
                    .SetProperty(x => x.StartedAt, (DateTimeOffset?)null), ct);
            if (timedOut > 0)
                logger.LogWarning("[TFQUEUE] BULK_STALE_REQUEUE api={ApiInstance} caller={Worker} count={Count} runningTimeoutMinutes={RunningTimeoutMinutes} maxAttempts={MaxAttempts}",
                    CodeQueueTrace.ApiInstance, worker, timedOut, runningTimeoutMinutes, maxAttempts);

            while (true)
            {
                var candidateId = await db.ExecutionJobs
                    .Where(x => x.Kind == ExecutionJobKinds.Legacy && x.Status == "queued" && x.CompletedAt == null && x.AttemptCount < maxAttempts)
                    .OrderBy(x => x.CreatedAt)
                    .Select(x => x.Id)
                    .FirstOrDefaultAsync(ct);

                if (candidateId == Guid.Empty) return Microsoft.AspNetCore.Http.Results.Ok(new { job = (object?)null });

                var claimed = await db.ExecutionJobs
                    .Where(x => x.Id == candidateId && x.Status == "queued" && x.CompletedAt == null && x.AttemptCount < maxAttempts)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(x => x.Status, "running")
                        .SetProperty(x => x.StartedAt, now)
                        .SetProperty(x => x.AttemptCount, x => x.AttemptCount + 1), ct);

                if (claimed == 0)
                {
                    logger.LogDebug("[TFQUEUE] CLAIM_RACE api={ApiInstance} worker={Worker} candidate={JobId}",
                        CodeQueueTrace.ApiInstance, worker, candidateId);
                    continue;
                }

                var job = await db.ExecutionJobs.AsNoTracking().FirstAsync(x => x.Id == candidateId, ct);
                logger.LogInformation(
                    "[TFQUEUE] CLAIMED api={ApiInstance} worker={Worker} job={JobId} submission={SubmissionId} language={Language} attempt={Attempt} maxAttempts={MaxAttempts} createdAt={CreatedAt:O} waitSeconds={WaitSeconds} startedAt={StartedAt:O}",
                    CodeQueueTrace.ApiInstance, worker, job.Id, job.SubmissionId, job.Language,
                    job.AttemptCount, maxAttempts, job.CreatedAt, CodeQueueTrace.AgeSeconds(now, job.CreatedAt), job.StartedAt);
                return Microsoft.AspNetCore.Http.Results.Ok(new { job = ToJobDto(job) });
            }
        });

        app.MapPost("/api/internal/execution/jobs/{jobId:guid}/heartbeat", async (Guid jobId, ExecutionJobHeartbeatRequest request, ExecutionDbContext db, HttpContext http, ILoggerFactory logs, CancellationToken ct) =>
        {
            var worker = CodeQueueTrace.FromRequest(http);
            var logger = logs.CreateLogger("TaskForge.CodeQueue");
            if (request.AttemptCount <= 0)
                return Microsoft.AspNetCore.Http.Results.BadRequest(new { code = "INVALID_EXECUTION_ATTEMPT" });

            var updated = await db.ExecutionJobs
                .Where(x => x.Id == jobId && x.Kind == ExecutionJobKinds.Legacy &&
                    x.Status == "running" && x.AttemptCount == request.AttemptCount)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.StartedAt, DateTimeOffset.UtcNow), ct);
            if (updated == 1)
            {
                logger.LogDebug("[TFQUEUE] HEARTBEAT_OK api={ApiInstance} worker={Worker} job={JobId} attempt={Attempt}",
                    CodeQueueTrace.ApiInstance, worker, jobId, request.AttemptCount);
                return Microsoft.AspNetCore.Http.Results.Ok(new { ok = true });
            }
            var actual = await db.ExecutionJobs.AsNoTracking().Where(x => x.Id == jobId)
                .Select(x => new { x.Status, x.AttemptCount, x.CompletedAt }).FirstOrDefaultAsync(ct);
            logger.LogWarning("[TFQUEUE] HEARTBEAT_REJECTED api={ApiInstance} worker={Worker} job={JobId} requestedAttempt={RequestedAttempt} actualAttempt={ActualAttempt} actualStatus={ActualStatus} completedAt={CompletedAt:O}",
                CodeQueueTrace.ApiInstance, worker, jobId, request.AttemptCount, actual?.AttemptCount,
                actual?.Status ?? "missing", actual?.CompletedAt);
            return Microsoft.AspNetCore.Http.Results.Conflict(new { code = "EXECUTION_ATTEMPT_EXPIRED" });
        });

        app.MapPost("/api/internal/execution/jobs/{jobId:guid}/complete", async (Guid jobId, CompleteExecutionJobRequest request, ExecutionDbContext db, HttpContext http, ILoggerFactory logs, CancellationToken ct) =>
        {
            var worker = CodeQueueTrace.FromRequest(http);
            var logger = logs.CreateLogger("TaskForge.CodeQueue");
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var job = await db.ExecutionJobs
                .FromSqlInterpolated($"SELECT * FROM \"ExecutionJobs\" WHERE \"Id\" = {jobId} FOR UPDATE")
                .SingleOrDefaultAsync(ct);
            if (job == null) return Microsoft.AspNetCore.Http.Results.NotFound(new { message = "Execution job not found", code = "EXECUTION_JOB_NOT_FOUND" });
            if (job.Kind != ExecutionJobKinds.Legacy) return Microsoft.AspNetCore.Http.Results.Conflict(new { code = "EXECUTION_CAPABILITY_MISMATCH" });
            if (request.AttemptCount.HasValue &&
                (job.AttemptCount != request.AttemptCount.Value ||
                 (job.Status != "running" && job.Status != request.Status && job.Status != "delivered")))
            {
                logger.LogWarning("[TFQUEUE] COMPLETE_REJECTED api={ApiInstance} worker={Worker} job={JobId} submission={SubmissionId} requestedAttempt={RequestedAttempt} actualAttempt={ActualAttempt} actualStatus={ActualStatus} incomingStatus={IncomingStatus}",
                    CodeQueueTrace.ApiInstance, worker, jobId, job.SubmissionId, request.AttemptCount,
                    job.AttemptCount, job.Status, request.Status);
                return Microsoft.AspNetCore.Http.Results.Conflict(new { code = "EXECUTION_ATTEMPT_EXPIRED" });
            }

            // Replayed completion from the same worker is harmless. The row lock
            // serializes it with watchdog expiry and result creation.
            if (request.AttemptCount.HasValue && job.Status is not "running")
            {
                logger.LogInformation("[TFQUEUE] COMPLETE_REPLAY api={ApiInstance} worker={Worker} job={JobId} submission={SubmissionId} attempt={Attempt} status={Status}",
                    CodeQueueTrace.ApiInstance, worker, job.Id, job.SubmissionId, request.AttemptCount, job.Status);
                return Microsoft.AspNetCore.Http.Results.Ok(new { ok = true, id = job.Id, status = job.Status });
            }

            job.Status = string.IsNullOrWhiteSpace(request.Status) ? "completed" : request.Status.Trim();
            job.CompletedAt = DateTimeOffset.UtcNow;

            var result = await db.ExecutionResults.FirstOrDefaultAsync(x => x.JobId == jobId, ct);
            if (result == null)
            {
                result = new ExecutionResult { JobId = jobId };
                db.ExecutionResults.Add(result);
            }

            result.Status = job.Status;
            result.Stdout = request.Stdout;
            result.Stderr = request.Stderr;
            result.ExitCode = request.ExitCode;
            result.Score = System.Math.Clamp(request.Score, 0, 100);
            result.Passed = request.Passed;
            result.DurationMs = System.Math.Max(0, request.DurationMs);
            result.ResultJson = request.Result.HasValue ? request.Result.Value.GetRawText() : null;

            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            logger.LogInformation("[TFQUEUE] COMPLETED api={ApiInstance} worker={Worker} job={JobId} submission={SubmissionId} attempt={Attempt} verdict={Verdict} score={Score} durationMs={DurationMs} elapsedSeconds={ElapsedSeconds}",
                CodeQueueTrace.ApiInstance, worker, job.Id, job.SubmissionId, job.AttemptCount,
                job.Status, result.Score, result.DurationMs, CodeQueueTrace.AgeSeconds(job.CompletedAt.Value, job.CreatedAt));
            return Microsoft.AspNetCore.Http.Results.Ok(new { ok = true, id = job.Id, status = job.Status });
        });

        return app;
    }
}
