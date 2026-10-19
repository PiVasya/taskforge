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
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<SolutionsDbContext>();
                var now = DateTimeOffset.UtcNow;
                var preparingBefore = now.AddMinutes(-Math.Clamp(cfg.GetValue("ExecutionQueue:PreparingTimeoutMinutes", 15), 5, 1440));
                var pendingBefore = now.AddMinutes(-Math.Clamp(cfg.GetValue("ExecutionQueue:PendingTimeoutMinutes", 90), 30, 10080));
                var rows = await db.Submissions.AsNoTracking()
                    .Where(x => x.SqlSpecVersionId == null &&
                        ((x.Status == "Preparing" && x.CreatedAt < preparingBefore) ||
                         ((x.Status == "Queued" || x.Status == "Running") && x.CreatedAt < pendingBefore)))
                    .OrderBy(x => x.CreatedAt)
                    .Select(x => new { x.Id, x.UserId, x.AssignmentId, x.Status })
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
                    logger.LogWarning("Code submission {SubmissionId} expired from {PreviousStatus}.", row.Id, row.Status);
                    if (row.UserId.HasValue)
                        await live.PublishAsync("code", row.Id, row.UserId.Value, row.AssignmentId, "JudgeUnavailable", 0);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning(ex, "Orphaned code submission scan failed."); }
            try { await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
