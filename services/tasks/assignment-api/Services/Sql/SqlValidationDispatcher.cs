using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TaskForge.Sql;
using TaskForge.Tasks.Api.Data;
using TaskForge.Tasks.Api.Domain.Sql;

namespace TaskForge.Tasks.Api.Services.Sql;

// Pending receipts are the durable outbox. Lost wakeups/HTTP responses do not lose validation work.
internal sealed class SqlValidationDispatcher(IServiceScopeFactory scopes, IHttpClientFactory clients,
    IConfiguration cfg, ILogger<SqlValidationDispatcher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();
                var ids = await db.SqlExpectedArtifacts.AsNoTracking()
                    .Where(x => x.Status == "pending" || x.Status == "validating")
                    .OrderBy(x => x.UpdatedAt).Select(x => x.Id).Take(24).ToListAsync(stoppingToken);
                await Parallel.ForEachAsync(ids, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = stoppingToken }, async (id, _) =>
                {
                    try { await Advance(id, stoppingToken); }
                    catch (DbUpdateConcurrencyException) { }
                    catch (HttpRequestException) { }
                    catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested) { }
                    catch (Exception ex) { logger.LogWarning("SQL validation {ReceiptId} could not advance: {ErrorType}", id, ex.GetType().Name); }
                });
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning("SQL validation dispatcher unavailable: {ErrorType}", ex.GetType().Name); }
            try { await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task Advance(Guid id, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();
        var receipt = await db.SqlExpectedArtifacts.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (receipt is null || receipt.Status is "valid" or "invalid") return;
        // Rotate the polling order even if the network is unavailable or a job is still queued.
        receipt.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        var target = await db.SqlAssignmentEngineTargets.AsNoTracking().SingleAsync(x => x.Id == receipt.EngineTargetId, ct);
        var baseUrl = SqlHttp.Url(cfg, "ExecutionApi", "http://execution-api:8080");
        if (receipt.ExecutionJobId is null)
        {
            var payload = await SqlTaskService.Payload(db, target.SpecVersionId, target.EngineProfileId, false, ct);
            var created = await SqlHttp.Send<SqlJobView>(clients, cfg, HttpMethod.Post, baseUrl + "/api/internal/execution/sql-jobs",
                new SqlCreateJob("sql-materialize", $"sql:materialize:{receipt.ValidationRunId:N}", null, null, payload), ct);
            if (created is null) return;
            receipt.ExecutionJobId = created.Id; receipt.Status = "validating";
            await db.SaveChangesAsync(ct);
            return;
        }
        var job = await SqlHttp.Send<SqlJobView>(clients, cfg, HttpMethod.Get,
            baseUrl + $"/api/internal/execution/sql-jobs/{receipt.ExecutionJobId:D}", null, ct);
        if (job is null || !SqlWire.IsTerminal(job.Status) || job.Result is null) return;
        if (job.Payload.ValidationRunId != receipt.ValidationRunId || job.Payload.EngineTargetId != receipt.EngineTargetId) return;
        var result = job.Result.Value;
        var validDataset = result.TryGetProperty("datasetValid", out var datasetValid) && datasetValid.ValueKind == JsonValueKind.True;
        var datasetReceipt = await db.SqlDatasetEngineValidations.SingleAsync(x => x.DatasetVersionId == job.Payload.DatasetVersionId && x.EngineProfileId == target.EngineProfileId, ct);
        if (datasetReceipt.Status != "valid")
        {
            datasetReceipt.Status = validDataset ? "valid" : "invalid";
            datasetReceipt.ValidatedAt = DateTimeOffset.UtcNow;
            datasetReceipt.ErrorCode = validDataset ? null : "SQL_DATASET_INVALID";
            datasetReceipt.ExecutionJobId = job.Id;
            datasetReceipt.DiagnosticJson = validDataset ? null : SafeDiagnostic(result);
        }
        if (validDataset && job.Status == "Validated" && result.TryGetProperty("expected", out var expected) && expected.ValueKind == JsonValueKind.Object)
        {
            var json = expected.GetRawText();
            if (Encoding.UTF8.GetByteCount(json) > 2_097_152)
            {
                receipt.Status = "invalid"; receipt.ErrorCode = "SQL_EXPECTED_SIZE";
                receipt.DiagnosticJson = SqlWire.Serialize(new { code = "SQL_EXPECTED_SIZE", message = "The expected artifact exceeds its 2 MB budget." });
                receipt.ValidatedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(ct);
                return;
            }
            receipt.ExpectedJson = json; receipt.ContentHash = SqlCanonicalWire.Hash(expected);
            receipt.ByteLength = Encoding.ASCII.GetByteCount(SqlCanonicalWire.Encode(expected));
            receipt.Status = "valid"; receipt.ErrorCode = null; receipt.DiagnosticJson = null;
        }
        else
        {
            receipt.Status = "invalid"; receipt.ErrorCode = "SQL_REFERENCE_INVALID";
            receipt.DiagnosticJson = SafeDiagnostic(result);
        }
        receipt.ValidatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        logger.LogInformation("SQL validation {ReceiptId} engine={EngineId} status={Status}", receipt.Id, target.EngineProfileId, receipt.Status);
    }

    private static string SafeDiagnostic(JsonElement result)
    {
        if (result.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
        {
            var code = error.TryGetProperty("code", out var c) ? c.GetString() : "SQL_VALIDATION_FAILED";
            var message = error.TryGetProperty("message", out var m) ? m.GetString() : "Engine validation failed.";
            return SqlWire.Serialize(new { code, message = message is { Length: > 4000 } ? message[..4000] : message });
        }
        return SqlWire.Serialize(new { code = "SQL_VALIDATION_FAILED", message = "Engine validation did not produce an expected artifact." });
    }
}
