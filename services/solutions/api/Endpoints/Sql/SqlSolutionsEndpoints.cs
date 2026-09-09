using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TaskForge.Sql;
using TaskForge.Solutions.Api.Data;
using TaskForge.Solutions.Api.Domain;
using TaskForge.Solutions.Api.Services.Sql;
using static TaskForge.Solutions.Api.Services.Access.SolutionsApiAccessService;
using static TaskForge.Solutions.Api.Services.Common.SolutionsApiCommonService;
using static TaskForge.Solutions.Api.Services.Mapping.SolutionsApiMappingService;
using static TaskForge.Solutions.Api.Services.Results.SolutionsApiResultsService;

namespace TaskForge.Solutions.Api.Endpoints;

internal static partial class SolutionsApiEndpoints
{
    private sealed record SqlVerdictInput(Guid JobId, Guid SpecVersionId, Guid EngineProfileId,
        string Verdict, bool Passed, int Score, JsonElement Result);

    private static WebApplication MapSqlSolutionsEndpoints(WebApplication app)
    {
        var group = app.MapGroup("/api/solutions/sql").AddEndpointFilter<SqlSolutionsFilter>();
        group.MapPost("/run", async (SqlAttemptInput request, HttpContext http, IConfiguration cfg,
            SolutionsDbContext db, IHttpClientFactory factory, CancellationToken ct) =>
        {
            if (CheckUserRateLimit(http, cfg, "solution-submit") is { } limited) return limited;
            var userId = CurrentUserId(http, cfg);
            if (!userId.HasValue) return Unauthorized();
            ValidateSqlAttempt(request);
            if (!await CanAccessSql(request.AssignmentId, userId.Value, http, cfg, factory, ct)) return Microsoft.AspNetCore.Http.Results.NotFound();
            var payload = await SqlSubmissionService.Published(factory, cfg, request.AssignmentId, request.EngineProfileId, ct);
            payload.Source = request.Sql;
            // Preview never contains private reference SQL, expected answers or a graded submission.
            payload.Expected = null; payload.ExpectedContentHash = null; payload.ReferenceSql = null;
            var job = await SqlHttp.Send<SqlJobView>(factory, cfg, HttpMethod.Post,
                $"{SqlSubmissionService.ExecutionUrl(cfg)}/api/internal/execution/sql-jobs",
                new SqlCreateJob("sql-preview", $"sql:preview:{userId.Value:N}:{request.RequestId:N}", null, userId, payload), ct);
            return Microsoft.AspNetCore.Http.Results.Accepted(value: new { jobId = job!.Id, status = job.Status });
        });
        group.MapGet("/previews/{jobId:guid}", async (Guid jobId, HttpContext http, IConfiguration cfg,
            IHttpClientFactory factory, CancellationToken ct) =>
        {
            var userId = CurrentUserId(http, cfg);
            if (!userId.HasValue) return Unauthorized();
            var job = await SqlHttp.Send<SqlJobView>(factory, cfg, HttpMethod.Get,
                $"{SqlSubmissionService.ExecutionUrl(cfg)}/api/internal/execution/sql-jobs/{jobId}", null, ct);
            if (job is null || job.Kind != "sql-preview" || job.UserId != userId) return Microsoft.AspNetCore.Http.Results.NotFound();
            return Microsoft.AspNetCore.Http.Results.Ok(new { jobId, status = job.Status, pending = !SqlWire.IsTerminal(job.Status), result = job.Result });
        });
        group.MapPost("/check", async (SqlAttemptInput request, HttpContext http, IConfiguration cfg,
            SolutionsDbContext db, IHttpClientFactory factory, CancellationToken ct) =>
        {
            if (CheckUserRateLimit(http, cfg, "solution-submit") is { } limited) return limited;
            var userId = CurrentUserId(http, cfg);
            if (!userId.HasValue) return Unauthorized();
            ValidateSqlAttempt(request);
            if (!await CanAccessSql(request.AssignmentId, userId.Value, http, cfg, factory, ct)) return Microsoft.AspNetCore.Http.Results.NotFound();
            var id = SqlSubmissionService.RequestSubmissionId(userId.Value, request.RequestId);
            var existing = await db.Submissions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
            if (existing is not null) return ExistingSqlSubmission(existing, request, userId.Value);
            // Pin the version before consuming energy or saving a submission.
            var payload = await SqlSubmissionService.Published(factory, cfg, request.AssignmentId, request.EngineProfileId, ct);
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var key = BitConverter.ToInt64(userId.Value.ToByteArray(), 0);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({key})", ct);
            existing = await db.Submissions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
            if (existing is not null) return ExistingSqlSubmission(existing, request, userId.Value);
            var charged = !HasUnlimitedTaskEnergy(http, cfg);
            if (charged)
            {
                var policy = QuotaPolicy(cfg, "tasks");
                var quota = await ConsumeQuotaAsync(db, userId.Value, "tasks", policy.Capacity, policy.Interval, 1, ct);
                WriteQuotaHeaders(http.Response, quota.quota);
                if (!quota.consumed) return QuotaExceeded(quota.quota);
            }
            var sub = new SolutionSubmission
            {
                Id = id, AssignmentId = request.AssignmentId, UserId = userId,
                Language = "sql", Code = request.Sql, Status = "Preparing", Score = 0,
                ExecutionTarget = payload.Profile.Fingerprint, SqlSpecVersionId = payload.SpecVersionId,
                SqlEngineProfileId = payload.Profile.Id,
                ResultJson = SqlWire.Serialize(new { verdict = "Preparing", kind = "sql", pending = true, energyCharged = charged })
            };
            db.Submissions.Add(sub);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            // The outbox above makes the remaining network operation recoverable.
            try { await SqlSubmissionService.Dispatch(db, sub, factory, cfg, ct); }
            catch (HttpRequestException) { }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            var response = await db.Submissions.AsNoTracking().SingleAsync(x => x.Id == id, ct);
            return Microsoft.AspNetCore.Http.Results.Accepted(value: ToDto(response));
        });
        app.MapPost("/api/internal/solutions/submissions/{submissionId:guid}/sql-verdict", async
            (Guid submissionId, SqlVerdictInput input, SolutionsDbContext db, IConfiguration cfg, CancellationToken ct) =>
        {
            if (input.Verdict is not ("Accepted" or "WrongAnswer" or "RuntimeError" or "TimeLimitExceeded" or "OutputLimitExceeded" or "JudgeUnavailable")
                || input.Passed != (input.Verdict == "Accepted") || input.Score != (input.Passed ? 100 : 0)
                || input.Result.ValueKind != JsonValueKind.Object || input.Result.GetRawText().Length > 3_000_000)
                return Microsoft.AspNetCore.Http.Results.BadRequest(new { code = "INVALID_SQL_VERDICT" });
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var sub = await db.Submissions.FromSqlInterpolated($"SELECT * FROM \"SolutionSubmissions\" WHERE \"Id\" = {submissionId} FOR UPDATE").SingleOrDefaultAsync(ct);
            if (sub is null) return Microsoft.AspNetCore.Http.Results.NotFound();
            if (sub.SqlSpecVersionId != input.SpecVersionId || sub.SqlEngineProfileId != input.EngineProfileId || input.JobId == Guid.Empty)
                return Microsoft.AspNetCore.Http.Results.Conflict(new { code = "SQL_BINDING_MISMATCH" });
            // SQL queue owns durable terminal delivery; legacy /verdict cannot mutate this submission.
            await SqlSubmissionService.Finish(db, sub, cfg, input.Verdict, input.Result, ct);
            await tx.CommitAsync(ct);
            return Microsoft.AspNetCore.Http.Results.Ok(new { ok = true, submissionId, status = sub.Status });
        });
        return app;
    }

    private static async Task<bool> CanAccessSql(Guid assignmentId, Guid userId, HttpContext http,
        IConfiguration cfg, IHttpClientFactory factory, CancellationToken ct)
        => IsEditor(http, cfg) || (await LoadAssignmentAccessAsync(assignmentId, userId, cfg, factory, ct))?.CanSubmit == true;

    private static void ValidateSqlAttempt(SqlAttemptInput request)
    {
        if (request.AssignmentId == Guid.Empty || request.EngineProfileId == Guid.Empty || request.RequestId == Guid.Empty
            || string.IsNullOrWhiteSpace(request.Sql) || request.Sql.Length > SqlWire.MaxSourceLength)
            throw new ArgumentException("Assignment, engine profile, request id and bounded non-empty SQL are required.");
    }
    private static IResult ExistingSqlSubmission(SolutionSubmission sub, SqlAttemptInput request, Guid userId)
        => sub.UserId != userId || sub.AssignmentId != request.AssignmentId || sub.SqlEngineProfileId != request.EngineProfileId || sub.Code != request.Sql
            ? Microsoft.AspNetCore.Http.Results.Conflict(new { code = "SQL_REQUEST_ID_REUSED" })
            : Microsoft.AspNetCore.Http.Results.Ok(ToDto(sub));
}

internal sealed class SqlSolutionsFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try { return await next(context); }
        catch (ArgumentException e) { return Results.BadRequest(new { code = "SQL_INVALID_INPUT", message = e.Message }); }
        catch (JsonException) { return Results.BadRequest(new { code = "SQL_INVALID_JSON" }); }
        catch (HttpRequestException e) when (e.StatusCode == HttpStatusCode.NotFound) { return Results.NotFound(); }
        catch (HttpRequestException e) when (e.StatusCode == HttpStatusCode.Conflict) { return Results.Conflict(new { code = "SQL_NOT_READY", message = "This SQL assignment is not validated for the selected engine." }); }
        catch (HttpRequestException e) when (e.StatusCode == HttpStatusCode.TooManyRequests) { return Results.Json(new { code = "SQL_QUEUE_FULL" }, statusCode: 429); }
        catch (HttpRequestException) { return Results.Json(new { code = "SQL_SERVICE_UNAVAILABLE" }, statusCode: 503); }
        catch (OperationCanceledException) when (!context.HttpContext.RequestAborted.IsCancellationRequested) { return Results.Json(new { code = "SQL_SERVICE_TIMEOUT" }, statusCode: 503); }
    }
}
