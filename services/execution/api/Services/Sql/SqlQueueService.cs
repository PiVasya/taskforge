using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using TaskForge.Execution.Api.Data;
using TaskForge.Execution.Api.Domain;
using TaskForge.Sql;

namespace TaskForge.Execution.Api.Services.Sql;

internal sealed class SqlWorkerDirectory
{
    private readonly ConcurrentDictionary<string, (SqlCapabilities Capabilities, DateTimeOffset Seen)> workers = new();
    internal void Update(SqlCapabilities caps) => workers[caps.WorkerId] = (caps, DateTimeOffset.UtcNow);
    internal bool Available(string target) => workers.Values.Any(x => x.Seen > DateTimeOffset.UtcNow.AddSeconds(-45) && x.Capabilities.Targets.Contains(target, StringComparer.Ordinal));
    internal object[] View() => workers.Values.Where(x => x.Seen > DateTimeOffset.UtcNow.AddSeconds(-45))
        .Select(x => (object)new { x.Capabilities.WorkerId, x.Capabilities.Targets, x.Capabilities.Concurrency, lastSeenAt = x.Seen }).ToArray();
}

internal sealed class SqlWakeups
{
    internal readonly Channel<bool> Pending = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    internal void Signal() => Pending.Writer.TryWrite(true);
}

internal static class SqlQueueService
{
    internal static SqlJobView View(ExecutionJob job, ExecutionResult? result = null) => new(job.Id, job.SubmissionId,
        job.UserId, job.Kind, job.Status, job.LeaseToken, job.LeaseExpiresAt,
        JsonSerializer.Deserialize<SqlJobPayload>(job.PayloadJson!, SqlWire.Json)!,
        result?.ResultJson is null ? null : JsonDocument.Parse(result.ResultJson).RootElement.Clone(), job.CreatedAt);

    internal static void Validate(SqlCreateJob request)
    {
        var p = request.Payload ?? throw new ArgumentException("SQL payload is required.");
        if (!SqlWire.IsSqlKind(request.Kind) || p.ContractVersion != SqlWire.Version || p.AssignmentId == Guid.Empty
            || p.SpecVersionId == Guid.Empty || p.DatasetVersionId == Guid.Empty || p.EngineTargetId == Guid.Empty
            || p.Profile is null || !SqlWire.IsHash(p.Profile.Fingerprint) || !SqlWire.IsHash(p.MaterializationKey)
            || !SqlWire.IsHash(p.ArtifactKey) || !SqlWire.IsHash(p.DatasetHash)
            || p.Profile.AdapterVersion != SqlWire.AdapterVersion || p.Profile.Engine is not ("postgresql" or "mysql" or "sqlite"))
            throw new ArgumentException("Invalid SQL execution binding.");
        if (string.IsNullOrWhiteSpace(request.DeduplicationKey) || request.DeduplicationKey.Length > 160
            || !request.DeduplicationKey.StartsWith("sql:", StringComparison.Ordinal)) throw new ArgumentException("Invalid idempotency key.");
        if (p.Limits is null || p.Comparison is null || p.StateCheck is null || p.SchemaCheck is null) throw new ArgumentException("SQL limits and settings are required.");
        p.Limits.Validate(); p.Comparison.Validate();
        if (p.Mode is not ("result" or "state" or "schema")) throw new ArgumentException("Invalid SQL mode.");
        SqlPortableValidator.Definition(new SqlDatasetVersionInput(p.Definition, p.Seed, p.EngineOverrides));
        if (request.Kind == "sql-materialize")
        {
            if (request.SubmissionId is not null || request.UserId is not null || p.ValidationRunId is null || p.ValidationRunId == Guid.Empty
                || string.IsNullOrWhiteSpace(p.ReferenceSql) || p.ReferenceSql.Length > SqlWire.MaxSourceLength)
                throw new ArgumentException("Invalid materialization job.");
        }
        else
        {
            if (request.UserId is null || request.UserId == Guid.Empty || string.IsNullOrWhiteSpace(p.Source)
                || p.Source.Length > SqlWire.MaxSourceLength || p.ReferenceSql is not null)
                throw new ArgumentException("Invalid student SQL job.");
            if (request.Kind == "sql-check" && (request.SubmissionId is null || request.SubmissionId == Guid.Empty || p.Expected is null || !SqlWire.IsHash(p.ExpectedContentHash)))
                throw new ArgumentException("A graded check must pin a validated expected artifact and submission.");
            if (request.Kind == "sql-preview" && (request.SubmissionId is not null || p.Expected is not null))
                throw new ArgumentException("A preview cannot contain a graded submission or hidden answer.");
        }
        if (SqlWire.Serialize(p).Length > 4_500_000) throw new ArgumentException("SQL job payload is too large.");
    }

    internal static async Task Fail(ExecutionDbContext db, Guid id, string code, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var job = await db.ExecutionJobs.FromSqlInterpolated($"SELECT * FROM \"ExecutionJobs\" WHERE \"Id\"={id} FOR UPDATE").SingleOrDefaultAsync(ct);
        if (job is null || !SqlWire.IsSqlKind(job.Kind) || SqlWire.IsTerminal(job.Status)) return;
        job.Status = "JudgeUnavailable"; job.CompletedAt = DateTimeOffset.UtcNow;
        db.ExecutionResults.Add(new ExecutionResult { JobId = id, Status = job.Status, Passed = false, Score = 0,
            ResultJson = SqlWire.Serialize(new { contractVersion = 1, verdict = job.Status, passed = false, error = new { code, message = "The SQL execution service did not complete this job. Retry when the engine is available." } }) });
        await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
    }
}

internal sealed class SqlQueueMaintenance(IServiceScopeFactory scopes, IHttpClientFactory clients,
    IConfiguration cfg, SqlWakeups wakeups, ILogger<SqlQueueMaintenance> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var lastGc = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ExecutionDbContext>();
                var now = DateTimeOffset.UtcNow;
                var expired = await db.ExecutionJobs.AsNoTracking().Where(x => x.Kind.StartsWith("sql-") &&
                    ((x.Status == "running" && x.LeaseExpiresAt < now) || (x.Status == "queued" && x.CreatedAt < now.AddMinutes(-15))))
                    .OrderBy(x => x.CreatedAt).Take(64).ToListAsync(stoppingToken);
                foreach (var job in expired)
                {
                    if (job.AttemptCount >= 3 || job.CreatedAt < now.AddMinutes(-15))
                        await SqlQueueService.Fail(db, job.Id, "SQL_JOB_EXPIRED", stoppingToken);
                    else
                    {
                        await db.ExecutionJobs.Where(x => x.Id == job.Id && x.Status == "running" && x.LeaseExpiresAt < now)
                            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, "queued").SetProperty(x => x.StartedAt, (DateTimeOffset?)null)
                                .SetProperty(x => x.ClaimedByWorkerId, (string?)null).SetProperty(x => x.LeaseToken, (Guid?)null)
                                .SetProperty(x => x.LeaseExpiresAt, (DateTimeOffset?)null), stoppingToken);
                        wakeups.Signal();
                    }
                }
                var deliveries = await (from job in db.ExecutionJobs.AsNoTracking()
                    join result in db.ExecutionResults.AsNoTracking() on job.Id equals result.JobId
                    where job.Kind == "sql-check" && job.Status != "delivered"
                    orderby job.CompletedAt select new { job, result }).Take(16).ToListAsync(stoppingToken);
                await Parallel.ForEachAsync(deliveries, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = stoppingToken }, async (delivery, _) =>
                {
                    using var deliveryScope = scopes.CreateScope();
                    var deliveryDb = deliveryScope.ServiceProvider.GetRequiredService<ExecutionDbContext>();
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    deadline.CancelAfter(TimeSpan.FromSeconds(3));
                    try
                    {
                        var payload = JsonSerializer.Deserialize<SqlJobPayload>(delivery.job.PayloadJson!, SqlWire.Json)!;
                        var body = new { jobId = delivery.job.Id, specVersionId = payload.SpecVersionId, engineProfileId = payload.Profile.Id,
                            verdict = delivery.result.Status, passed = delivery.result.Passed, score = delivery.result.Score,
                            result = delivery.result.ResultJson is null ? (JsonElement?)null : JsonDocument.Parse(delivery.result.ResultJson).RootElement.Clone() };
                        await SqlHttp.Send<JsonElement>(clients, cfg, HttpMethod.Post,
                            SqlHttp.Url(cfg, "SolutionsApi", "http://solutions-api:8080") + $"/api/internal/solutions/submissions/{delivery.job.SubmissionId:D}/sql-verdict", body, deadline.Token);
                        await deliveryDb.ExecutionJobs.Where(x => x.Id == delivery.job.Id && x.Status != "delivered")
                            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, "delivered"), stoppingToken);
                    }
                    catch (HttpRequestException) { }
                    catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested) { }
                });
                if (now - lastGc > TimeSpan.FromHours(1))
                {
                    var cutoff = now.AddDays(-14);
                    var ids = await db.ExecutionJobs.AsNoTracking().Where(x => x.Kind.StartsWith("sql-") && x.CompletedAt < cutoff
                        && (x.Kind != "sql-check" || x.Status == "delivered")).Select(x => x.Id).Take(1000).ToArrayAsync(stoppingToken);
                    if (ids.Length > 0)
                    {
                        await db.ExecutionResults.Where(x => ids.Contains(x.JobId)).ExecuteDeleteAsync(stoppingToken);
                        await db.ExecutionJobs.Where(x => ids.Contains(x.Id)).ExecuteDeleteAsync(stoppingToken);
                    }
                    lastGc = now;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning("SQL queue maintenance unavailable: {ErrorType}", ex.GetType().Name); }
            try { await Task.Delay(500, stoppingToken); } catch (OperationCanceledException) { break; }
        }
    }
}

internal sealed class SqlRabbitWakeupPublisher(SqlWakeups wakeups, IHttpClientFactory clients, IConfiguration cfg,
    ILogger<SqlRabbitWakeupPublisher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Management API is only a notification transport, never the durable queue.
        var url = cfg["Sql:RabbitManagementUrl"];
        if (string.IsNullOrWhiteSpace(url)) return;
        var vhost = Uri.EscapeDataString(cfg["Sql:RabbitVhost"] ?? "/");
        var endpoint = url.TrimEnd('/') + $"/api/exchanges/{vhost}/taskforge.sql.wakeup";
        var user = cfg["Sql:RabbitUser"] ?? "guest";
        var password = cfg["Sql:RabbitPassword"] ?? "guest";
        var auth = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(user + ":" + password)));
        while (await wakeups.Pending.Reader.WaitToReadAsync(stoppingToken))
        {
            wakeups.Pending.Reader.TryRead(out _);
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                deadline.CancelAfter(TimeSpan.FromSeconds(2));
                using var declare = new HttpRequestMessage(HttpMethod.Put, endpoint)
                { Content = JsonContent.Create(new { type = "fanout", durable = true, auto_delete = false, @internal = false, arguments = new { } }) };
                declare.Headers.Authorization = auth;
                using var declared = await clients.CreateClient().SendAsync(declare, deadline.Token);
                declared.EnsureSuccessStatusCode();
                using var publish = new HttpRequestMessage(HttpMethod.Post, endpoint + "/publish")
                { Content = JsonContent.Create(new { properties = new { }, routing_key = "", payload = "{}", payload_encoding = "string" }) };
                publish.Headers.Authorization = auth;
                using var published = await clients.CreateClient().SendAsync(publish, deadline.Token);
                published.EnsureSuccessStatusCode();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogDebug("SQL wakeup missed; fallback polling remains active: {ErrorType}", ex.GetType().Name); }
        }
    }
}
