using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TaskForge.Sql;
using TaskForge.Solutions.Api.Data;
using TaskForge.Solutions.Api.Domain;
using static TaskForge.Solutions.Api.Services.Common.SolutionsApiCommonService;
using static TaskForge.Solutions.Api.Services.Results.SolutionsApiResultsService;

namespace TaskForge.Solutions.Api.Services.Sql;

internal static class SqlSubmissionService
{
    internal static Guid RequestSubmissionId(Guid userId, Guid requestId)
        => new(SHA256.HashData(Encoding.UTF8.GetBytes($"taskforge:sql-check:v1:{userId:D}:{requestId:D}"))[..16]);

    internal static string ExecutionUrl(IConfiguration cfg) => SqlHttp.Url(cfg, "ExecutionApi", "http://execution-api:8080");
    internal static string TasksUrl(IConfiguration cfg) => SqlHttp.Url(cfg, "TasksApi", "http://tasks-api:8080");
    internal sealed record SqlCapacity(bool Available);
    internal sealed record JudgeSpecResponse(Guid Id, string Type, SqlJobPayload? Sql);

    internal static async Task<SqlJobPayload> Published(IHttpClientFactory factory, IConfiguration cfg,
        Guid assignmentId, Guid engineId, CancellationToken ct)
    {
        var spec = await SqlHttp.Send<JudgeSpecResponse>(factory, cfg, HttpMethod.Get,
            $"{TasksUrl(cfg)}/api/internal/assignments/{assignmentId}/judge-spec?engineProfileId={engineId}", null, ct);
        if (spec?.Type != "sql-test" || spec.Sql is null) throw new ArgumentException("A published SQL assignment and selected engine are required.");
        if (spec.Sql.Profile.Id != engineId || spec.Sql.AssignmentId != assignmentId || spec.Sql.ReferenceSql is not null)
            throw new InvalidOperationException("Invalid private judge binding.");
        var capacity = await SqlHttp.Send<SqlCapacity>(factory, cfg, HttpMethod.Get,
            $"{ExecutionUrl(cfg)}/api/internal/execution/sql-capabilities?target={spec.Sql.Profile.Fingerprint}", null, ct);
        if (capacity?.Available != true) throw new HttpRequestException("No healthy SQL worker supports the pinned runtime.", null, System.Net.HttpStatusCode.ServiceUnavailable);
        return spec.Sql;
    }

    internal static async Task Dispatch(SolutionsDbContext db, SolutionSubmission sub, IHttpClientFactory factory,
        IConfiguration cfg, CancellationToken ct)
    {
        if (sub.Status != "Preparing" || sub.SqlSpecVersionId is null || sub.SqlEngineProfileId is null) return;
        var payload = await SqlHttp.Send<SqlJobPayload>(factory, cfg, HttpMethod.Get,
            $"{TasksUrl(cfg)}/api/internal/sql/specs/{sub.SqlSpecVersionId}/targets/{sub.SqlEngineProfileId}", null, ct)
            ?? throw new InvalidOperationException("Pinned SQL spec is unavailable.");
        if (payload.AssignmentId != sub.AssignmentId || payload.Profile.Fingerprint != sub.ExecutionTarget || payload.ReferenceSql is not null)
            throw new InvalidOperationException("Pinned SQL submission binding changed.");
        payload.Source = sub.Code;
        var job = await SqlHttp.Send<SqlJobView>(factory, cfg, HttpMethod.Post,
            $"{ExecutionUrl(cfg)}/api/internal/execution/sql-jobs",
            new SqlCreateJob("sql-check", $"sql:check:{sub.Id:N}", sub.Id, sub.UserId, payload), ct)
            ?? throw new InvalidOperationException("No durable job receipt.");
        // A very fast completion can arrive before this update. Never downgrade it to Queued.
        var charged = WasCharged(sub.ResultJson);
        var pending = SqlWire.Serialize(new { verdict = "Queued", pending = true, executionJobId = job.Id, energyCharged = charged });
        await db.Submissions.Where(x => x.Id == sub.Id && x.Status == "Preparing")
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, "Queued").SetProperty(x => x.ResultJson, pending), ct);
    }

    internal static bool WasCharged(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return false;
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("energyCharged", out var value) && value.ValueKind == JsonValueKind.True;
    }

    internal static async Task Finish(SolutionsDbContext db, SolutionSubmission sub, IConfiguration cfg,
        string verdict, JsonElement result, CancellationToken ct)
    {
        if (!IsPendingVerdict(sub.Status)) return;
        var charged = WasCharged(sub.ResultJson);
        // Caller owns a transaction and a submission row lock. Retried delivery cannot refund twice.
        if (verdict == "JudgeUnavailable" && charged && sub.UserId.HasValue)
        {
            var policy = QuotaPolicy(cfg, "tasks");
            await RefundQuotaAsync(db, sub.UserId.Value, "tasks", policy.Capacity, policy.Interval, 1, ct);
        }
        sub.Status = verdict;
        sub.Score = verdict == "Accepted" ? 100 : 0;
        sub.ResultJson = SqlWire.Serialize(new
        {
            kind = "sql", verdict, score = sub.Score, passed = verdict == "Accepted", pending = false,
            engineProfileId = sub.SqlEngineProfileId, specVersionId = sub.SqlSpecVersionId,
            executionTarget = sub.ExecutionTarget, energyRefunded = verdict == "JudgeUnavailable" && charged,
            sql = result
        });
        if (sub.UserId.HasValue) await MarkRatingDirtyAsync(db, sub.UserId.Value, "sql-verdict", sub.AssignmentId, ct);
        await db.SaveChangesAsync(ct);
    }
}

// Preparing SQL submissions are a transactional outbox. A crash after saving/charging but before
// execution enqueue is recovered here; execution deduplication makes retry safe across instances.
internal sealed class SqlSubmissionDispatcher(IServiceScopeFactory scopes, ILogger<SqlSubmissionDispatcher> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var listScope = scopes.CreateScope();
                var db = listScope.ServiceProvider.GetRequiredService<SolutionsDbContext>();
                var ids = await db.Submissions.AsNoTracking()
                    .Where(x => x.SqlSpecVersionId != null && x.Status == "Preparing")
                    .OrderBy(x => x.CreatedAt).Select(x => x.Id).Take(32).ToListAsync(stoppingToken);
                await Parallel.ForEachAsync(ids, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = stoppingToken }, async (id, _) =>
                {
                    using var scope = scopes.CreateScope();
                    var scopedDb = scope.ServiceProvider.GetRequiredService<SolutionsDbContext>();
                    var cfg = scope.ServiceProvider.GetRequiredService<IConfiguration>();
                    var sub = await scopedDb.Submissions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, stoppingToken);
                    if (sub is null || sub.Status != "Preparing") return;
                    try
                    {
                        using var deliveryDeadline = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                        deliveryDeadline.CancelAfter(TimeSpan.FromSeconds(3));
                        await SqlSubmissionService.Dispatch(scopedDb, sub,
                            scope.ServiceProvider.GetRequiredService<IHttpClientFactory>(), cfg, deliveryDeadline.Token);
                    }
                    catch (Exception e) when (e is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                    {
                        log.LogWarning("SQL submission enqueue retry {SubmissionId} ({ErrorType})", id, e.GetType().Name);
                        if (sub.CreatedAt < DateTimeOffset.UtcNow.AddMinutes(-15))
                        {
                            await using var tx = await scopedDb.Database.BeginTransactionAsync(stoppingToken);
                            var locked = await scopedDb.Submissions.FromSqlInterpolated($"SELECT * FROM \"SolutionSubmissions\" WHERE \"Id\" = {id} FOR UPDATE").SingleAsync(stoppingToken);
                            if (locked.Status == "Preparing")
                                await SqlSubmissionService.Finish(scopedDb, locked, cfg, "JudgeUnavailable",
                                    SqlWire.Element(new { error = new { code = "SQL_QUEUE_UNAVAILABLE", message = "SQL execution is temporarily unavailable. No grade was awarded." } }), stoppingToken);
                            await tx.CommitAsync(stoppingToken);
                        }
                    }
                });
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception e) { log.LogWarning("SQL submission outbox scan failed ({ErrorType})", e.GetType().Name); }
            try { await Task.Delay(1000, stoppingToken); } catch (OperationCanceledException) { break; }
        }
    }
}
