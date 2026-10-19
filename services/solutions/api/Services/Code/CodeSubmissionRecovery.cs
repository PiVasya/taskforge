using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TaskForge.Realtime;
using TaskForge.Solutions.Api.Data;

namespace TaskForge.Solutions.Api.Services.Code;

// Preparing code submissions can be orphaned before execution-api accepts a job.
// The long-stop also closes historical Queued/Running rows with no durable job.
internal sealed class CodeSubmissionRecovery(
    IServiceScopeFactory scopes,
    IConfiguration cfg,
    AdminSolutionEventPublisher live,
    ILogger<CodeSubmissionRecovery> logger) : BackgroundService
{
    private DateTimeOffset _nextDiagnostics = DateTimeOffset.MinValue;
    private static readonly string Instance = BuildInstance();

    private static string BuildInstance()
    {
        var label = Environment.GetEnvironmentVariable("TASKFORGE_NODE_LABEL") ?? "unlabeled";
        if (label.Length is < 1 or > 32 || label.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.')))
            label = "unlabeled";
        var host = Environment.MachineName;
        if (host.Length > 40) host = host[..40];
        return $"{label}/{host}/{Guid.NewGuid():N}";
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("[TFQUEUE] SUBMISSION_WATCHDOG_START api={ApiInstance} preparingTimeoutMinutes={PreparingTimeout} pendingTimeoutMinutes={PendingTimeout}",
            Instance, Math.Clamp(cfg.GetValue("ExecutionQueue:PreparingTimeoutMinutes", 15), 5, 1440),
            Math.Clamp(cfg.GetValue("ExecutionQueue:PendingTimeoutMinutes", 90), 30, 10080));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<SolutionsDbContext>();
                var now = DateTimeOffset.UtcNow;
                var preparingBefore = now.AddMinutes(-Math.Clamp(cfg.GetValue("ExecutionQueue:PreparingTimeoutMinutes", 15), 5, 1440));
                var pendingBefore = now.AddMinutes(-Math.Clamp(cfg.GetValue("ExecutionQueue:PendingTimeoutMinutes", 90), 30, 10080));
                if (now >= _nextDiagnostics)
                {
                    _nextDiagnostics = now.AddSeconds(Math.Clamp(cfg.GetValue("ExecutionQueue:DiagnosticsIntervalSeconds", 60), 15, 300));
                    await LogPendingSnapshotAsync(db, now, stoppingToken);
                }
                var rows = await db.Submissions.AsNoTracking()
                    .Where(x => x.SqlSpecVersionId == null &&
                        ((x.Status == "Preparing" && x.CreatedAt < preparingBefore) ||
                         ((x.Status == "Queued" || x.Status == "Running") && x.CreatedAt < pendingBefore)))
                    .OrderBy(x => x.CreatedAt)
                    .Select(x => new { x.Id, x.UserId, x.AssignmentId, x.Status, x.CreatedAt })
                    .Take(48)
                    .ToArrayAsync(stoppingToken);
                foreach (var row in rows)
                {
                    var isPreparing = row.Status == "Preparing";
                    var before = isPreparing ? preparingBefore : pendingBefore;
                    var result = JsonSerializer.Serialize(new
                    {
                        verdict = "JudgeUnavailable",
                        status = "JudgeUnavailable",
                        pending = false,
                        code = isPreparing ? "CODE_SUBMISSION_NOT_ENQUEUED" : "CODE_SUBMISSION_EXPIRED",
                        message = isPreparing
                            ? "Не удалось запустить проверку решения. Отправьте его повторно."
                            : "Проверка решения не завершилась вовремя. Отправьте его повторно."
                    }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                    var updated = await db.Submissions
                        .Where(x => x.Id == row.Id && x.Status == row.Status && x.SqlSpecVersionId == null && x.CreatedAt < before)
                        .ExecuteUpdateAsync(s => s
                            .SetProperty(x => x.Status, "JudgeUnavailable")
                            .SetProperty(x => x.Score, 0)
                            .SetProperty(x => x.ResultJson, result), stoppingToken);
                    if (updated == 0) continue;
                    logger.LogWarning("[TFQUEUE] SUBMISSION_EXPIRED api={ApiInstance} submission={SubmissionId} assignment={AssignmentId} previousStatus={PreviousStatus} reason={Reason} ageSeconds={AgeSeconds}",
                        Instance, row.Id, row.AssignmentId, row.Status,
                        isPreparing ? "not_enqueued" : "pending_timeout", Math.Max(0, (long)(DateTimeOffset.UtcNow - row.CreatedAt).TotalSeconds));
                    if (row.UserId.HasValue)
                        await live.PublishAsync("code", row.Id, row.UserId.Value, row.AssignmentId, "JudgeUnavailable", 0);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning(ex, "[TFQUEUE] SUBMISSION_WATCHDOG_ERROR api={ApiInstance} errorType={ErrorType}", Instance, ex.GetType().Name); }
            try { await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task LogPendingSnapshotAsync(SolutionsDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        var pending = db.Submissions.AsNoTracking().Where(x => x.SqlSpecVersionId == null &&
            (x.Status == "Preparing" || x.Status == "Queued" || x.Status == "Running"));
        var counts = await pending.GroupBy(x => x.Status)
            .Select(group => new { Status = group.Key, Count = group.Count(), Oldest = group.Min(x => x.CreatedAt) })
            .ToArrayAsync(ct);
        var preparing = counts.Where(x => x.Status == "Preparing").Sum(x => x.Count);
        var queued = counts.Where(x => x.Status == "Queued").Sum(x => x.Count);
        var running = counts.Where(x => x.Status == "Running").Sum(x => x.Count);
        var oldestSeconds = counts.Length == 0 ? 0 : Math.Max(0, (long)(now - counts.Min(x => x.Oldest)).TotalSeconds);
        var samples = await pending.OrderBy(x => x.CreatedAt).Take(8)
            .Select(x => new { x.Id, x.AssignmentId, x.Status, x.CreatedAt })
            .ToArrayAsync(ct);
        var details = string.Join(" | ", samples.Select(x =>
            $"submission={x.Id};assignment={x.AssignmentId};status={x.Status};ageSec={Math.Max(0, (long)(now - x.CreatedAt).TotalSeconds)}"));
        if (oldestSeconds >= 60)
            logger.LogWarning("[TFQUEUE] SUBMISSION_SNAPSHOT_ALERT api={ApiInstance} preparing={Preparing} queued={Queued} running={Running} oldestAgeSeconds={OldestAgeSeconds} samples={Samples}",
                Instance, preparing, queued, running, oldestSeconds, details);
        else
            logger.LogInformation("[TFQUEUE] SUBMISSION_SNAPSHOT api={ApiInstance} preparing={Preparing} queued={Queued} running={Running} oldestAgeSeconds={OldestAgeSeconds} samples={Samples}",
                Instance, preparing, queued, running, oldestSeconds, details);
    }
}
