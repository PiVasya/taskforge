using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TaskForge.Execution.Api.Data;
using TaskForge.Execution.Api.Domain;

namespace TaskForge.Execution.Api.Services.Legacy;

// Recovers legacy code jobs after worker failure and redelivers completed verdicts.
// A submission is considered delivered only after solutions-api acknowledges it.
internal sealed class LegacyExecutionQueueMaintenance(
    IServiceScopeFactory scopes,
    IHttpClientFactory httpClients,
    IConfiguration cfg,
    ILogger<LegacyExecutionQueueMaintenance> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ExpireJobsAsync(stoppingToken);
                await DeliverResultsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Legacy execution queue recovery failed; will retry.");
            }

            try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task ExpireJobsAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ExecutionDbContext>();
        var now = DateTimeOffset.UtcNow;
        var maxAttempts = Math.Clamp(cfg.GetValue("ExecutionQueue:MaxAttempts", 3), 1, 10);
        var runningTimeout = TimeSpan.FromMinutes(Math.Clamp(cfg.GetValue("ExecutionQueue:RunningTimeoutMinutes", 5), 1, 60));
        var queuedTimeout = TimeSpan.FromMinutes(Math.Clamp(cfg.GetValue("ExecutionQueue:QueuedTimeoutMinutes", 30), 5, 1440));
        var runningBefore = now - runningTimeout;
        var queuedBefore = now - queuedTimeout;

        var candidates = await db.ExecutionJobs.AsNoTracking()
            .Where(x => x.Kind == ExecutionJobKinds.Legacy && x.CompletedAt == null &&
                ((x.Status == "running" &&
                    ((x.StartedAt != null && x.StartedAt < runningBefore) ||
                     (x.StartedAt == null && x.CreatedAt < runningBefore))) ||
                 (x.Status == "queued" && (x.AttemptCount >= maxAttempts || x.CreatedAt < queuedBefore))))
            .OrderBy(x => x.CreatedAt)
            .Select(x => x.Id)
            .Take(64)
            .ToArrayAsync(ct);

        foreach (var id in candidates)
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            // Avoid blocking verdict completion while the watchdog is inspecting a row.
            var job = await db.ExecutionJobs
                .FromSqlInterpolated($"SELECT * FROM \"ExecutionJobs\" WHERE \"Id\" = {id} FOR UPDATE SKIP LOCKED")
                .SingleOrDefaultAsync(ct);
            if (job is null) { await transaction.RollbackAsync(ct); continue; }

            var action = LegacyQueuePolicy.Expiration(job, DateTimeOffset.UtcNow, maxAttempts, runningTimeout, queuedTimeout);
            if (action == LegacyExpirationAction.Requeue)
            {
                job.Status = "queued";
                job.StartedAt = null;
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                logger.LogWarning("Requeued stalled code execution job {JobId}; attempt {Attempt}/{Max}.",
                    job.Id, job.AttemptCount, maxAttempts);
            }
            else if (action == LegacyExpirationAction.Fail)
            {
                // An already persisted worker result takes precedence over a timeout.
                var existing = await db.ExecutionResults.FirstOrDefaultAsync(x => x.JobId == id, ct);
                if (existing is not null)
                {
                    job.Status = existing.Status;
                }
                else
                {
                    job.Status = "JudgeUnavailable";
                    db.ExecutionResults.Add(new ExecutionResult
                    {
                        JobId = job.Id,
                        Status = job.Status,
                        Score = 0,
                        Passed = false,
                        ResultJson = JsonSerializer.Serialize(new
                        {
                            verdict = "JudgeUnavailable",
                            code = job.StartedAt.HasValue ? "EXECUTION_ATTEMPTS_EXHAUSTED" : "EXECUTION_QUEUE_TIMEOUT",
                            message = "Проверка не завершилась. Повторите отправку решения, когда сервис проверки будет доступен."
                        }, JsonOptions)
                    });
                }
                job.CompletedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                logger.LogWarning("Code execution job {JobId} expired; submission={SubmissionId} status={Status} attempts={Attempt}.",
                    job.Id, job.SubmissionId, job.Status, job.AttemptCount);
            }
            else
            {
                await transaction.RollbackAsync(ct);
            }
            db.ChangeTracker.Clear();
        }
    }

    private async Task DeliverResultsAsync(CancellationToken ct)
    {
        // Old solutions-api versions ignore the reconciliation-only flag and
        // could overwrite real verdicts during a staggered service rollout.
        // Delivery begins only after the receiving API advertises this contract.
        var url = (cfg["Services:SolutionsApi"] ?? cfg["ServiceUrls:SolutionsApi"] ?? "http://solutions-api:8080").TrimEnd('/');
        if (!await ReconciliationAvailableAsync(url, ct)) return;

        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ExecutionDbContext>();
        var deliveries = await (
            from job in db.ExecutionJobs.AsNoTracking()
            join result in db.ExecutionResults.AsNoTracking() on job.Id equals result.JobId
            where job.Kind == ExecutionJobKinds.Legacy && job.Status != "delivered" && job.CompletedAt != null
            orderby job.CompletedAt descending
            select new { job.Id, job.SubmissionId, JobStatus = job.Status, job.CompletedAt,
                result.Score, result.Passed, result.ResultJson, Verdict = result.Status }).Take(24).ToArrayAsync(ct);

        await Parallel.ForEachAsync(deliveries,
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, async (delivery, token) =>
        {
            // An admin may have removed a submission, in which case no verdict is needed.
            var succeeded = delivery.SubmissionId is null;
            if (!succeeded)
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(cfg.GetValue("ExecutionQueue:DeliveryTimeoutSeconds", 5), 2, 30)));
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Post,
                        $"{url}/api/internal/solutions/submissions/{delivery.SubmissionId:D}/verdict")
                    {
                        Content = JsonContent.Create(new
                        {
                            verdict = delivery.Verdict,
                            score = delivery.Score,
                            message = "Результат проверки восстановлен из execution-api.",
                            result = SolutionResult(delivery.Verdict, delivery.Score, delivery.Passed, delivery.ResultJson),
                            onlyIfPending = true
                        }, options: JsonOptions)
                    };
                    AddInternalKey(request);
                    using var response = await httpClients.CreateClient().SendAsync(request, deadline.Token);
                    succeeded = response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound;
                    if (!succeeded)
                        logger.LogWarning("Verdict delivery failed for code job {JobId}: HTTP {StatusCode}.", delivery.Id, (int)response.StatusCode);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    logger.LogWarning("Verdict delivery deferred for code job {JobId}: {Error}.", delivery.Id, ex.GetType().Name);
                }
            }

            if (!succeeded) return;
            using var deliveredScope = scopes.CreateScope();
            var deliveredDb = deliveredScope.ServiceProvider.GetRequiredService<ExecutionDbContext>();
            var count = await deliveredDb.ExecutionJobs
                .Where(x => x.Id == delivery.Id && x.Status == delivery.JobStatus && x.CompletedAt == delivery.CompletedAt)
                .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.Status, "delivered"), token);
            if (count > 0)
                logger.LogInformation("Delivered legacy code verdict for job {JobId}, submission={SubmissionId}, verdict={Verdict}.",
                    delivery.Id, delivery.SubmissionId, delivery.Verdict);
        });
    }

    private async Task<bool> ReconciliationAvailableAsync(string baseUrl, CancellationToken ct)
    {
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(3));
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"{baseUrl}/api/internal/solutions/code-verdict-reconciliation/ready");
            AddInternalKey(request);
            using var response = await httpClients.CreateClient().SendAsync(request, deadline.Token);
            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogDebug("Code verdict reconciliation unavailable: {Error}.", ex.GetType().Name);
            return false;
        }
    }

    private void AddInternalKey(HttpRequestMessage request)
    {
        var key = cfg["InternalApi:Key"] ?? cfg["TaskForgeInternalApi:ApiKey"] ?? cfg["TaskForge:InternalKey"] ?? Environment.GetEnvironmentVariable("TASKFORGE_INTERNAL_KEY");
        if (!string.IsNullOrWhiteSpace(key)) request.Headers.TryAddWithoutValidation("X-Internal-Key", key);
    }

    internal static JsonElement SolutionResult(string verdict, int score, bool passed, string? resultJson)
    {
        JsonElement? raw = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(resultJson))
                raw = JsonSerializer.Deserialize<JsonElement>(resultJson);
        }
        catch (JsonException) { }

        // New workers persist the exact solution DTO alongside the execution
        // result; older rows contain only the raw runner response.
        if (raw.HasValue && raw.Value.ValueKind == JsonValueKind.Object &&
            raw.Value.TryGetProperty("verdict", out var storedVerdict) &&
            storedVerdict.ValueKind == JsonValueKind.String &&
            raw.Value.TryGetProperty("passedAllTests", out _))
            return raw.Value.Clone();

        JsonElement? cases = null;
        if (raw.HasValue && raw.Value.ValueKind == JsonValueKind.Object)
        {
            if (raw.Value.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
                cases = results.Clone();
            else if (raw.Value.TryGetProperty("cases", out var alternate) && alternate.ValueKind == JsonValueKind.Array)
                cases = alternate.Clone();
        }

        return JsonSerializer.SerializeToElement(new
        {
            verdict,
            score,
            passedAllTests = passed,
            compileError = string.Equals(verdict, "CompileError", StringComparison.OrdinalIgnoreCase),
            policyFailed = string.Equals(verdict, "PolicyFailed", StringComparison.OrdinalIgnoreCase),
            results = cases,
            cases,
            raw
        }, JsonOptions);
    }
}
