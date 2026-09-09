using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TaskForge.Execution.Api.Data;
using TaskForge.Execution.Api.Domain;

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

        app.MapPost("/api/internal/execution/jobs", async (CreateExecutionJobRequest request, ExecutionDbContext db, CancellationToken ct) =>
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
            return Microsoft.AspNetCore.Http.Results.Ok(ToJobDto(job));
        });

        app.MapPost("/api/internal/execution/jobs/claim-next", async (ExecutionDbContext db, IConfiguration cfg, CancellationToken ct) =>
        {
            var now = DateTimeOffset.UtcNow;
            var maxAttempts = System.Math.Clamp(cfg.GetValue("ExecutionQueue:MaxAttempts", 3), 1, 10);
            var runningTimeoutMinutes = System.Math.Clamp(cfg.GetValue("ExecutionQueue:RunningTimeoutMinutes", 5), 1, 60);
            var staleBefore = now.AddMinutes(-runningTimeoutMinutes);

            await db.ExecutionJobs
                .Where(x => x.Kind == ExecutionJobKinds.Legacy && x.Status == "running" && x.StartedAt != null && x.StartedAt < staleBefore && x.AttemptCount < maxAttempts)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.Status, "queued")
                    .SetProperty(x => x.StartedAt, (DateTimeOffset?)null), ct);

            while (true)
            {
                var candidateId = await db.ExecutionJobs
                    .Where(x => x.Kind == ExecutionJobKinds.Legacy && x.Status == "queued" && x.AttemptCount < maxAttempts)
                    .OrderBy(x => x.CreatedAt)
                    .Select(x => x.Id)
                    .FirstOrDefaultAsync(ct);

                if (candidateId == Guid.Empty) return Microsoft.AspNetCore.Http.Results.Ok(new { job = (object?)null });

                var claimed = await db.ExecutionJobs
                    .Where(x => x.Id == candidateId && x.Status == "queued")
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(x => x.Status, "running")
                        .SetProperty(x => x.StartedAt, now)
                        .SetProperty(x => x.AttemptCount, x => x.AttemptCount + 1), ct);

                if (claimed == 0) continue;

                var job = await db.ExecutionJobs.AsNoTracking().FirstAsync(x => x.Id == candidateId, ct);
                return Microsoft.AspNetCore.Http.Results.Ok(new { job = ToJobDto(job) });
            }
        });

        app.MapPost("/api/internal/execution/jobs/{jobId:guid}/complete", async (Guid jobId, CompleteExecutionJobRequest request, ExecutionDbContext db, CancellationToken ct) =>
        {
            var job = await db.ExecutionJobs.FirstOrDefaultAsync(x => x.Id == jobId, ct);
            if (job == null) return Microsoft.AspNetCore.Http.Results.NotFound(new { message = "Execution job not found", code = "EXECUTION_JOB_NOT_FOUND" });

            if (job.Kind != ExecutionJobKinds.Legacy) return Microsoft.AspNetCore.Http.Results.Conflict(new { code = "EXECUTION_CAPABILITY_MISMATCH" });

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
            return Microsoft.AspNetCore.Http.Results.Ok(new { job = ToJobDto(job), result });
        });

        return app;
    }
}
