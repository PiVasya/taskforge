using System.Data;
using TaskForge.Tasks.Api.Services.Sql;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TaskForge.Shared.Import;
using TaskForge.Tasks.Api.Data;
using TaskForge.Tasks.Api.Domain;
using TaskForge.Tasks.Api.Endpoints;

namespace TaskForge.Tasks.Api.Services.Transfer;

internal sealed class GraphTransferWorker(IServiceScopeFactory scopes, IConfiguration cfg, ILogger<GraphTransferWorker> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.WhenAll(
        LaneAsync("metadata", stoppingToken), LaneAsync("content", stoppingToken), CleanupAsync(stoppingToken));

    private async Task LaneAsync(string lane, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                Guid[] ids;
                await using (var scope = scopes.CreateAsyncScope())
                {
                    var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();
                    ids = await db.TaskGraphTransfers.AsNoTracking().Where(x => x.Lane == lane && (x.State == "queued" || x.State == "running"))
                        .OrderBy(x => x.CreatedAt).Select(x => x.Id).Take(64).ToArrayAsync(stop);
                }
                foreach (var id in ids)
                {
                    stop.ThrowIfCancellationRequested();
                    if (await RunOneAsync(id, stop)) break;
                }
                await Task.Delay(ids.Length == 0 ? 1000 : 200, stop);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            catch (Exception error)
            {
                logger.LogError(error, "TASK_GRAPH_TRANSFER_QUEUE Lane={Lane}", lane);
                await Task.Delay(3000, stop);
            }
        }
    }

    private async Task<bool> RunOneAsync(Guid id, CancellationToken stop)
    {
        // A session advisory lock, not a time-based lease: a slow commit must
        // never allow two workers to mutate the same operation concurrently.
        await using var lockScope = scopes.CreateAsyncScope();
        var lockDb = lockScope.ServiceProvider.GetRequiredService<TasksDbContext>();
        await using var guard = await PostgresImportLock.TryAcquireAsync(lockDb, $"task-graph-transfer:{id:D}", stop);
        if (guard is null) return false;
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();
        var job = await db.TaskGraphTransfers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, stop);
        if (job is null || job.State is not ("queued" or "running")) return false;
        if (job.CancelRequested)
        {
            await FinishAsync(id, "cancelled", null, "TRANSFER_CANCELLED", 409, 0, stop);
            return true;
        }
        var now = DateTimeOffset.UtcNow;
        await db.TaskGraphTransfers.Where(x => x.Id == id).ExecuteUpdateAsync(s => s
            .SetProperty(x => x.State, "running").SetProperty(x => x.Phase, "authorize").SetProperty(x => x.Percent, 1)
            .SetProperty(x => x.AttemptCount, x => x.AttemptCount + 1).SetProperty(x => x.StartedAt, now)
            .SetProperty(x => x.HeartbeatAt, now).SetProperty(x => x.UpdatedAt, now).SetProperty(x => x.ErrorJson, (string?)null), stop);
        await db.TaskGraphTransferParts.Where(x => x.TransferId == id).ExecuteDeleteAsync(stop);
        using var run = CancellationTokenSource.CreateLinkedTokenSource(stop);
        run.CancelAfter(TimeSpan.FromMinutes(global::System.Math.Clamp(cfg.GetValue("GraphTransfers:MaximumMinutes", 60), 1, 240)));
        using var monitor = CancellationTokenSource.CreateLinkedTokenSource(stop);
        var progress = new GraphTransferProgress(id, job.Kind, logger);
        progress.Report("authorize", 1);
        string? interruption = null;
        var heartbeat = HeartbeatAsync(id, lockDb, progress, run, code => Interlocked.CompareExchange(ref interruption, code, null), monitor.Token);
        logger.LogInformation("TASK_GRAPH_TRANSFER_BEGIN Transfer={TransferId} Operation={OperationId} Course={CourseId} Kind={Kind} Lane={Lane} Attempt={Attempt}",
            id, job.ImportOperationId, job.CourseId, job.Kind, job.Lane, job.AttemptCount + 1);
        try
        {
            var clients = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>();
            var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider, RequestAborted = run.Token,
                TraceIdentifier = $"graph-transfer:{id:D}" };
            progress.Attach(http);
            if (!await GraphTransferActor.RestoreAsync(http, job.UserId, cfg, clients, run.Token))
            {
                await FinishAsync(id, "failed", progress.Read(), "TRANSFER_ACCESS_REVOKED", 403, 0, stop);
                return true;
            }
            var result = await AssignmentApiEndpoints.ExecuteTransferAsync(job, http, cfg, db, clients, run.Token);
            var status = (result as IStatusCodeHttpResult)?.StatusCode ?? 200;
            var value = (result as IValueHttpResult)?.Value;
            if (status >= 400)
            {
                // Keep structured validation/permission issues, never HTTP headers or raw input.
                var error = JsonSerializer.Serialize(value ?? new { code = "TRANSFER_REJECTED" }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                await FinishAsync(id, "failed", progress.Read(), error, status, 0, stop, isJson: true);
            }
            else
            {
                progress.Report("result.write", 95);
                var bytes = await GraphTransferArtifacts.WriteAsync(id, value, scopes, progress, run.Token);
                await FinishAsync(id, "succeeded", progress.Read(), null, status, bytes, stop);
            }
        }
        catch (OperationCanceledException)
        {
            var requested = await ReadCancelAsync(id, CancellationToken.None);
            var state = stop.IsCancellationRequested ? "queued" : requested ? "cancelled" : "failed";
            var code = stop.IsCancellationRequested ? "TRANSFER_RESTART_PENDING" : requested ? "TRANSFER_CANCELLED" : interruption ?? "TRANSFER_EXECUTION_TIMEOUT";
            await FinishAsync(id, state, progress.Read(), code, 409, 0, CancellationToken.None);
            logger.LogWarning("TASK_GRAPH_TRANSFER_INTERRUPTED Transfer={TransferId} Phase={Phase} Reason={Reason}", id, progress.Read().Phase, code);
        }
        catch (Exception error)
        {
            logger.LogError(error, "TASK_GRAPH_TRANSFER_FAILED Transfer={TransferId} Phase={Phase} Item={Item}", id, progress.Read().Phase, progress.Read().CurrentItem);
            var (code, status) = ClassifyError(error);
            await FinishAsync(id, "failed", progress.Read(), code, status, 0, CancellationToken.None);
        }
        finally
        {
            monitor.Cancel();
            try { await heartbeat; } catch (OperationCanceledException) { }
            catch (Exception error) { logger.LogWarning(error, "TASK_GRAPH_TRANSFER_HEARTBEAT_STOP Transfer={TransferId}", id); }
        }
        return true;
    }

    internal static (string Code, int Status) ClassifyError(Exception error) => error switch
    {
        SqlAccessException => ("TRANSFER_ACCESS_REVOKED", 403),
        SqlNotFoundException => ("TRANSFER_RESOURCE_NOT_FOUND", 404),
        SqlNotReadyException or SqlNotPublishedException => ("TRANSFER_RESOURCE_NOT_READY", 409),
        DbUpdateConcurrencyException => ("TRANSFER_CONCURRENT_CHANGE", 409),
        DbUpdateException { InnerException: Npgsql.PostgresException { SqlState: "23505" } } => ("TRANSFER_DUPLICATE_ID", 409),
        HttpRequestException => ("TRANSFER_DEPENDENCY_UNAVAILABLE", 503),
        JsonException or ArgumentException => ("TRANSFER_INVALID_PAYLOAD", 400),
        InvalidOperationException e when e.Message == "TRANSFER_RESULT_TOO_LARGE" => ("TRANSFER_RESULT_TOO_LARGE", 413),
        InvalidOperationException e when e.Message.StartsWith("DATA_INTEGRITY_ERROR", StringComparison.Ordinal) => ("TRANSFER_DATA_INTEGRITY_ERROR", 409),
        _ => ("TRANSFER_EXECUTION_FAILED", 500)
    };

    private async Task HeartbeatAsync(Guid id, TasksDbContext lockDb, GraphTransferProgress progress,
        CancellationTokenSource run, Action<string> interrupted, CancellationToken stop)
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                await Task.Delay(1000, stop);
                if (lockDb.Database.GetDbConnection().State != ConnectionState.Open)
                    throw new InvalidOperationException("TRANSFER_LOCK_CONNECTION_LOST");
                await lockDb.Database.ExecuteSqlRawAsync("SELECT 1", stop);
                var value = progress.Read();
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();
                if (await db.TaskGraphTransfers.Where(x => x.Id == id).Select(x => x.CancelRequested).SingleAsync(stop))
                    run.Cancel();
                await db.TaskGraphTransfers.Where(x => x.Id == id && x.State == "running").ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.Phase, value.Phase).SetProperty(x => x.Percent, value.Percent)
                    .SetProperty(x => x.CompletedItems, value.CompletedItems).SetProperty(x => x.TotalItems, value.TotalItems)
                    .SetProperty(x => x.BytesWritten, value.BytesWritten).SetProperty(x => x.CurrentItem, value.CurrentItem)
                    .SetProperty(x => x.HeartbeatAt, DateTimeOffset.UtcNow).SetProperty(x => x.UpdatedAt, DateTimeOffset.UtcNow), stop);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception error)
        {
            logger.LogError(error, "TASK_GRAPH_TRANSFER_HEARTBEAT_FAILED Transfer={TransferId}", id);
            interrupted("TRANSFER_HEARTBEAT_LOST");
            run.Cancel();
        }
    }

    private async Task<bool> ReadCancelAsync(Guid id, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<TasksDbContext>().TaskGraphTransfers.Where(x => x.Id == id)
            .Select(x => x.CancelRequested).SingleAsync(ct);
    }

    private async Task FinishAsync(Guid id, string state, TransferProgressSnapshot? value, string? error,
        int status, long bytes, CancellationToken ct, bool isJson = false)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();
        var json = error is null ? null : isJson ? error : JsonSerializer.Serialize(new { code = error, transferId = id, phase = value?.Phase });
        var expires = DateTimeOffset.UtcNow.AddHours(global::System.Math.Clamp(cfg.GetValue("GraphTransfers:RetentionHours", 24), 1, 168));
        await db.TaskGraphTransfers.Where(x => x.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.State, state)
            .SetProperty(x => x.ExpiresAt, expires)
            .SetProperty(x => x.ErrorJson, json).SetProperty(x => x.ResultStatus, status).SetProperty(x => x.ResultBytes, bytes)
            .SetProperty(x => x.BytesWritten, bytes > 0 ? bytes : value == null ? 0 : value.BytesWritten)
            .SetProperty(x => x.Percent, state == "succeeded" ? 100 : value == null ? 0 : value.Percent)
            .SetProperty(x => x.Phase, state == "succeeded" ? "completed" : value == null ? "queued" : value.Phase)
            .SetProperty(x => x.CompletedItems, value == null ? 0 : value.CompletedItems)
            .SetProperty(x => x.TotalItems, value == null ? 0 : value.TotalItems)
            .SetProperty(x => x.CurrentItem, value == null ? null : value.CurrentItem)
            .SetProperty(x => x.FinishedAt, state == "queued" ? (DateTimeOffset?)null : DateTimeOffset.UtcNow)
            .SetProperty(x => x.UpdatedAt, DateTimeOffset.UtcNow), ct);
        logger.LogInformation("TASK_GRAPH_TRANSFER_END Transfer={TransferId} State={State} Phase={Phase} Status={Status} CompressedBytes={Bytes}",
            id, state, value?.Phase, status, bytes);
    }

    private async Task CleanupAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();
                var now = DateTimeOffset.UtcNow;
                await db.TaskGraphTransfers.Where(x => x.State == "expired" && x.ExpiresAt < now.AddDays(-7)).ExecuteDeleteAsync(stop);
                var expired = await db.TaskGraphTransfers.Where(x => x.ExpiresAt < now && x.State != "queued" && x.State != "running" && x.State != "expired")
                    .Select(x => x.Id).Take(50).ToArrayAsync(stop);
                foreach (var id in expired)
                {
                    await using var guard = await PostgresImportLock.TryAcquireAsync(db, $"task-graph-transfer:{id:D}", stop);
                    if (guard is null) continue;
                    if (!await db.TaskGraphTransfers.AnyAsync(x => x.Id == id && x.ExpiresAt < now
                        && x.State != "queued" && x.State != "running" && x.State != "expired", stop)) continue;
                    await db.TaskGraphTransferParts.Where(x => x.TransferId == id).ExecuteDeleteAsync(stop);
                    await db.TaskGraphTransfers.Where(x => x.Id == id).ExecuteUpdateAsync(s => s
                        .SetProperty(x => x.State, "expired").SetProperty(x => x.PayloadJson, "{}").SetProperty(x => x.ErrorJson, (string?)null), stop);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            catch (Exception error) { logger.LogWarning(error, "TASK_GRAPH_TRANSFER_RETENTION"); }
            await Task.Delay(TimeSpan.FromMinutes(5), stop);
        }
    }
}
