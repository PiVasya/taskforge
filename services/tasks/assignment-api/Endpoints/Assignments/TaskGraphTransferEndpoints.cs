using System.Text.Json;
using System.Text.Json.Nodes;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using TaskForge.Shared.Import;
using TaskForge.Tasks.Api.Data;
using TaskForge.Tasks.Api.Domain;
using TaskForge.Tasks.Api.Services.Transfer;
using static TaskForge.Tasks.Api.Services.Access.AssignmentApiAccessService;
using static TaskForge.Tasks.Api.Services.Common.AssignmentApiCommonService;
using static TaskForge.Tasks.Api.Services.Serialization.AssignmentTaskGraphJsonService;
using HttpResults = Microsoft.AspNetCore.Http.Results;

namespace TaskForge.Tasks.Api.Endpoints;

internal static partial class AssignmentApiEndpoints
{
    internal sealed record TransferStart(Guid TransferId, string Kind, JsonElement Payload, Guid? OperationId = null);
    internal sealed record TransferStatus(Guid TransferId, Guid CourseId, Guid UserId, Guid? OperationId,
        string Kind, string Lane, string State, string Phase, int Percent, int CompletedItems, int TotalItems,
        long BytesWritten, long ResultBytes, string? CurrentItem, string? ErrorJson, int? ResultStatus,
        bool CancelRequested, int AttemptCount, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
        DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt, DateTimeOffset? HeartbeatAt, DateTimeOffset ExpiresAt);

    // This projection is deliberately independent of the request and artifact columns.
    internal static readonly Expression<Func<TaskGraphTransfer, TransferStatus>> TransferStatusProjection = x => new(
        x.Id, x.CourseId, x.UserId, x.ImportOperationId, x.Kind, x.Lane, x.State, x.Phase, x.Percent,
        x.CompletedItems, x.TotalItems, x.BytesWritten, x.ResultBytes, x.CurrentItem, x.ErrorJson, x.ResultStatus,
        x.CancelRequested, x.AttemptCount, x.CreatedAt, x.UpdatedAt, x.StartedAt, x.FinishedAt, x.HeartbeatAt, x.ExpiresAt);

    internal static string TransferLane(string kind, JsonElement payload)
    {
        if (kind is "map") return "metadata";
        if (kind is "resume") return "content";
        if (kind is "export" or "snapshot")
        {
            var options = ReadTransferExportOptions(payload);
            return options.IncludeContent || options.IncludeChecks ? "content" : "metadata";
        }
        var graph = ReadGraphImportPayload(payload);
        if (!LooksLikeCanonicalGraph(graph) || !graph.TryGetProperty("scopes", out var scopes) || scopes.ValueKind != JsonValueKind.Array)
            return "content";
        var selected = scopes.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()).ToHashSet();
        var opts = ReadGraphImportOptions(payload);
        return (opts.UpdateContent && selected.Contains("content")) || (opts.UpdateChecks && selected.Contains("checks"))
            ? "content" : "metadata";
    }

    private static void MapTaskGraphTransferEndpoints(WebApplication app)
    {
        app.MapPost("/api/courses/{courseId:guid}/assignments/transfers", async (Guid courseId, TransferStart input,
            HttpContext http, IConfiguration cfg, TasksDbContext db, IHttpClientFactory clients, CancellationToken ct) =>
        {
            if (!await CanReadImportJournal(courseId, http, cfg, clients, ct)) return CourseEditForbidden();
            var user = RequireUser(http, cfg);
            if (!user.HasValue) return HttpResults.Unauthorized();
            if (input.TransferId == Guid.Empty || input.Kind is not ("export" or "snapshot" or "preview" or "import" or "resume" or "map")
                || input.Payload.ValueKind != JsonValueKind.Object
                || (input.Kind is "resume" or "map") && (!input.OperationId.HasValue || input.OperationId == Guid.Empty))
                return HttpResults.BadRequest(new { code = "TRANSFER_REQUEST_INVALID" });
            // The public gateway accepts up to 80 MiB; reserve a stricter job
            // bound here to keep retained requests and queue admission bounded.
            if (System.Text.Encoding.UTF8.GetByteCount(input.Payload.GetRawText()) > 24 * 1024 * 1024)
                return HttpResults.Json(new { code = "TRANSFER_PAYLOAD_TOO_LARGE", maximumBytes = 24 * 1024 * 1024 }, statusCode: 413);
            var payload = input.Payload;
            if (input.Kind == "import")
            {
                if (payload.ValueKind != JsonValueKind.Object) return HttpResults.BadRequest(new { code = "TRANSFER_REQUEST_INVALID" });
                var body = JsonNode.Parse(payload.GetRawText())!.AsObject();
                var operationId = input.OperationId ?? input.TransferId;
                if (operationId == Guid.Empty) return HttpResults.BadRequest(new { code = "TRANSFER_REQUEST_INVALID" });
                body["operationId"] = operationId.ToString("D");
                payload = JsonSerializer.SerializeToElement(body);
            }
            var hash = ImportJsonIdentity.Hash(JsonSerializer.SerializeToElement(new { input.Kind, input.OperationId, payload }));
            await using var admission = await PostgresImportLock.TryAcquireAsync(db, "task-graph-transfer-admission", ct);
            if (admission is null) return HttpResults.Json(new { code = "TRANSFER_QUEUE_BUSY" }, statusCode: 429);
            var existing = await db.TaskGraphTransfers.AsNoTracking().Where(x => x.Id == input.TransferId).Select(TransferStatusProjection).SingleOrDefaultAsync(ct);
            if (existing is not null)
            {
                if (existing.UserId != user.Value || existing.CourseId != courseId) return HttpResults.NotFound();
                var previousHash = await db.TaskGraphTransfers.Where(x => x.Id == input.TransferId).Select(x => x.RequestHash).SingleAsync(ct);
                return previousHash == hash ? HttpResults.Ok(existing)
                    : HttpResults.Conflict(new { code = "TRANSFER_PAYLOAD_CHANGED" });
            }
            var active = db.TaskGraphTransfers.Where(x => x.State == "queued" || x.State == "running");
            if (await active.CountAsync(ct) >= Math.Clamp(cfg.GetValue("GraphTransfers:QueueCapacity", 64), 2, 1000)
                || await active.CountAsync(x => x.UserId == user.Value, ct) >= 6)
                return HttpResults.Json(new { code = "TRANSFER_QUEUE_FULL" }, statusCode: 429);
            var opId = input.Kind == "import" ? input.OperationId ?? input.TransferId : input.OperationId;
            var job = new TaskGraphTransfer { Id = input.TransferId, CourseId = courseId, UserId = user.Value,
                ImportOperationId = opId, Kind = input.Kind, Lane = TransferLane(input.Kind, payload), RequestHash = hash,
                PayloadJson = payload.GetRawText(), ExpiresAt = DateTimeOffset.UtcNow.AddHours(Math.Clamp(cfg.GetValue("GraphTransfers:RetentionHours", 24), 1, 168)) };
            db.TaskGraphTransfers.Add(job);
            await db.SaveChangesAsync(ct);
            http.Response.Headers.CacheControl = "no-store";
            http.Response.Headers.RetryAfter = "1";
            var view = await db.TaskGraphTransfers.AsNoTracking().Where(x => x.Id == job.Id).Select(TransferStatusProjection).SingleAsync(ct);
            return HttpResults.Accepted($"/api/courses/{courseId:D}/assignments/transfers/{job.Id:D}", view);
        });

        app.MapGet("/api/courses/{courseId:guid}/assignments/transfers", async (Guid courseId,
            HttpContext http, IConfiguration cfg, TasksDbContext db, IHttpClientFactory clients, CancellationToken ct) =>
        {
            if (!IsEditor(http, cfg) || !await CanUserEditCourseAsync(courseId, http, cfg, clients, ct)) return CourseEditForbidden();
            var user = RequireUser(http, cfg);
            http.Response.Headers.CacheControl = "no-store";
            return HttpResults.Ok(await db.TaskGraphTransfers.AsNoTracking().Where(x => x.CourseId == courseId && x.UserId == user)
                .OrderByDescending(x => x.State == "queued" || x.State == "running").ThenByDescending(x => x.CreatedAt).Take(30).Select(TransferStatusProjection).ToListAsync(ct));
        });

        app.MapGet("/api/courses/{courseId:guid}/assignments/transfers/{transferId:guid}", async (Guid courseId, Guid transferId,
            HttpContext http, IConfiguration cfg, TasksDbContext db, IHttpClientFactory clients, CancellationToken ct) =>
        {
            if (!IsEditor(http, cfg) || !await CanUserEditCourseAsync(courseId, http, cfg, clients, ct)) return CourseEditForbidden();
            var user = RequireUser(http, cfg);
            var job = await db.TaskGraphTransfers.AsNoTracking().Where(x => x.Id == transferId && x.CourseId == courseId && x.UserId == user)
                .Select(TransferStatusProjection).SingleOrDefaultAsync(ct);
            http.Response.Headers.CacheControl = "no-store";
            return job is null ? HttpResults.NotFound() : HttpResults.Ok(job);
        });

        app.MapGet("/api/courses/{courseId:guid}/assignments/transfers/{transferId:guid}/result", async (Guid courseId, Guid transferId,
            HttpContext http, IConfiguration cfg, TasksDbContext db, IHttpClientFactory clients, IServiceScopeFactory scopes, CancellationToken ct) =>
        {
            if (!await CanReadImportJournal(courseId, http, cfg, clients, ct)) return CourseEditForbidden();
            var user = RequireUser(http, cfg);
            var job = await db.TaskGraphTransfers.AsNoTracking().Where(x => x.Id == transferId && x.CourseId == courseId && x.UserId == user)
                .Select(TransferStatusProjection).SingleOrDefaultAsync(ct);
            if (job is null) return HttpResults.NotFound();
            if (job.State == "expired" || job.ExpiresAt < DateTimeOffset.UtcNow) return HttpResults.StatusCode(410);
            if (job.State != "succeeded") return HttpResults.Conflict(new { code = "TRANSFER_RESULT_NOT_READY" });
            http.Response.Headers.CacheControl = "no-store";
            http.Response.Headers["X-Content-Type-Options"] = "nosniff";
            return new GraphTransferDownloadResult(transferId, scopes,
                job.Kind == "export" ? $"taskforge-{courseId:D}-task-graph.json" : null);
        });

        app.MapPost("/api/courses/{courseId:guid}/assignments/transfers/{transferId:guid}/cancel", async (Guid courseId, Guid transferId,
            HttpContext http, IConfiguration cfg, TasksDbContext db, IHttpClientFactory clients, CancellationToken ct) =>
        {
            if (!IsEditor(http, cfg) || !await CanUserEditCourseAsync(courseId, http, cfg, clients, ct)) return CourseEditForbidden();
            var user = RequireUser(http, cfg);
            var own = db.TaskGraphTransfers.Where(x => x.Id == transferId && x.CourseId == courseId && x.UserId == user);
            if (!await own.AnyAsync(ct)) return HttpResults.NotFound();
            await own.Where(x => x.State == "queued" || x.State == "running").ExecuteUpdateAsync(s => s
                .SetProperty(x => x.CancelRequested, true).SetProperty(x => x.UpdatedAt, DateTimeOffset.UtcNow), ct);
            return HttpResults.Accepted();
        });

        app.MapPost("/api/courses/{courseId:guid}/assignments/transfers/{transferId:guid}/retry", async (Guid courseId, Guid transferId,
            HttpContext http, IConfiguration cfg, TasksDbContext db, IHttpClientFactory clients, CancellationToken ct) =>
        {
            if (!await CanReadImportJournal(courseId, http, cfg, clients, ct)) return CourseEditForbidden();
            var user = RequireUser(http, cfg);
            if (!user.HasValue) return HttpResults.Unauthorized();
            await using var admission = await PostgresImportLock.TryAcquireAsync(db, "task-graph-transfer-admission", ct);
            if (admission is null) return HttpResults.Json(new { code = "TRANSFER_QUEUE_BUSY" }, statusCode: 429);
            var active = db.TaskGraphTransfers.Where(x => x.State == "queued" || x.State == "running");
            if (await active.CountAsync(ct) >= Math.Clamp(cfg.GetValue("GraphTransfers:QueueCapacity", 64), 2, 1000)
                || await active.CountAsync(x => x.UserId == user.Value, ct) >= 6)
                return HttpResults.Json(new { code = "TRANSFER_QUEUE_FULL" }, statusCode: 429);
            await using var guard = await PostgresImportLock.TryAcquireAsync(db, $"task-graph-transfer:{transferId:D}", ct);
            if (guard is null) return HttpResults.Conflict(new { code = "TRANSFER_STILL_RUNNING" });
            var own = db.TaskGraphTransfers.Where(x => x.Id == transferId && x.CourseId == courseId && x.UserId == user);
            var affected = await own.Where(x => (x.State == "failed" || x.State == "cancelled") && x.ExpiresAt > DateTimeOffset.UtcNow)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.State, "queued").SetProperty(x => x.CancelRequested, false)
                    .SetProperty(x => x.ErrorJson, (string?)null).SetProperty(x => x.Percent, 0).SetProperty(x => x.Phase, "queued")
                    .SetProperty(x => x.FinishedAt, (DateTimeOffset?)null).SetProperty(x => x.UpdatedAt, DateTimeOffset.UtcNow), ct);
            return affected == 1 ? HttpResults.Accepted() : HttpResults.Conflict(new { code = "TRANSFER_NOT_RETRYABLE" });
        });
    }

    internal static Task<IResult> ExecuteTransferAsync(TaskGraphTransfer job, HttpContext http,
        IConfiguration cfg, TasksDbContext db, IHttpClientFactory clients, CancellationToken ct)
    {
        var payload = ImportJsonIdentity.Read(job.PayloadJson);
        return job.Kind switch
        {
            "export" or "snapshot" => ExportTaskGraphCoreAsync(job.CourseId, ReadTransferExportOptions(payload), http, cfg, db, clients, ct),
            "preview" => PreviewTaskGraphAsync(job.CourseId, payload, http, cfg, db, clients, ct),
            "import" => JournaledTaskGraphImportAsync(job.CourseId, payload, http, cfg, db, clients, ct),
            "resume" => ResumeTaskGraphAsync(job.CourseId, job.ImportOperationId!.Value, http, cfg, db, clients, ct),
            "map" => SaveTaskGraphMapAsync(job.CourseId, job.ImportOperationId!.Value, payload, http, cfg, db, clients, ct),
            _ => Task.FromResult<IResult>(HttpResults.BadRequest(new { code = "TRANSFER_KIND_INVALID" }))
        };
    }
}
