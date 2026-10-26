using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using TaskForge.Shared.Import;
using TaskForge.Tasks.Api.Contracts;
using TaskForge.Tasks.Api.Data;
using TaskForge.Tasks.Api.Domain;
using TaskForge.Tasks.Api.Services.Sql;
using static TaskForge.Tasks.Api.Services.Access.AssignmentApiAccessService;
using static TaskForge.Tasks.Api.Services.Common.AssignmentApiCommonService;
using static TaskForge.Tasks.Api.Services.Mapping.AssignmentApiMappingService;
using static TaskForge.Tasks.Api.Services.Serialization.AssignmentTaskGraphJsonService;
using HttpResults = Microsoft.AspNetCore.Http.Results;

namespace TaskForge.Tasks.Api.Endpoints;

internal static partial class AssignmentApiEndpoints
{
    private static readonly JsonSerializerOptions ImportJournalJson = new(JsonSerializerDefaults.Web);

    private static object? JournalValue(string? json) => json is null ? null : ImportJsonIdentity.Read(json);
    private static IResult ImportBusy(Guid id) => HttpResults.Conflict(new { code = "IMPORT_OPERATION_BUSY", operationId = id,
        message = "Импорт уже выполняется. Проверьте состояние этой операции; не создавайте новый импорт." });

    private static async Task<bool> CanReadImportJournal(Guid courseId, HttpContext http, IConfiguration cfg,
        IHttpClientFactory clients, CancellationToken ct)
    {
        if (!IsEditor(http, cfg) || !await CanUserEditCourseAsync(courseId, http, cfg, clients, ct)) return false;
        var user = TaskForgeRequestSecurity.UserId(http, cfg);
        if (!user.HasValue) return false;
        var tree = await GetInternalAsync<CourseTreeResponse>(clients, cfg,
            ServiceUrl(cfg, "EducationApi", "http://education-api:8080"), $"/api/internal/courses/{courseId:D}/tree", ct);
        if (tree is null || tree.CourseIds.Length == 0) return false;
        var ids = tree.CourseIds.Append(courseId).Distinct().ToArray();
        var access = await LoadCourseAccessRowsAsync(ids.ToHashSet(), user.Value, clients, cfg, ct);
        var editable = access.Where(x => x.CanEdit).Select(x => x.CourseId).ToHashSet();
        return ids.All(editable.Contains);
    }

    private static IResult ImportStopped(Guid id) => HttpResults.Conflict(new { code = "IMPORT_OPERATION_STOPPED", operationId = id,
        message = "Операция прекращена без отката. Её нельзя продолжить." });

    private static IResult ReplayImport(TaskGraphImportOperation operation)
    {
        var value = JsonNode.Parse(operation.ResponseJson!)!.AsObject();
        value["operationId"] = operation.Id.ToString("D");
        value["operationState"] = operation.State;
        return HttpResults.Json(value, ImportJournalJson);
    }

    private static object ImportOperationView(TaskGraphImportOperation op) => new
    {
        operationId = op.Id, op.CourseId, op.State, op.RequestHash, op.AttemptCount, op.CreatedAt, op.UpdatedAt,
        response = JournalValue(op.ResponseJson), mapRequest = JournalValue(op.MapRequestJson),
        mapResponse = JournalValue(op.MapResponseJson), error = JournalValue(op.LastErrorJson),
        audit = JournalValue(op.AuditJson)
    };

    private static async Task<IResult> JournaledTaskGraphImportAsync(Guid courseId, JsonElement payload, HttpContext http,
        IConfiguration cfg, TasksDbContext db, IHttpClientFactory clients, CancellationToken ct)
    {
        if (!await CanReadImportJournal(courseId, http, cfg, clients, ct)) return CourseEditForbidden();
        var requestedUserId = RequireUser(http, cfg);
        if (!requestedUserId.HasValue) return HttpResults.Unauthorized();
        var userId = requestedUserId.Value;
        var operationId = Guid.NewGuid();
        if (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("operationId", out var idValue)
            && (idValue.ValueKind != JsonValueKind.String || !Guid.TryParse(idValue.GetString(), out operationId) || operationId == Guid.Empty))
            return HttpResults.BadRequest(new { code = "IMPORT_OPERATION_ID_INVALID", message = "operationId: нужен UUID." });
        http.Items[ImportOperationContextKey] = operationId;
        var prepareElapsed = Stopwatch.StartNew();
        LogImportPhase(http, prepareElapsed, "prepare.begin");
        var input = ReadGraphImportPayload(payload);
        // Invalid documents must never be repaired by ID reservation.
        ParsedGraph? requestGraph = null;
        if (LooksLikeCanonicalGraph(input))
        {
            var validation = ParseAndValidate(input, ReadGraphImportOptions(payload));
            if (validation.Issues.Count > 0) return HttpResults.BadRequest(new { code = "TASK_GRAPH_VALIDATION_FAILED", issues = validation.Issues });
            requestGraph = validation.Graph;
        }
        var options = payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("options", out var selected)
            ? selected.Clone() : JsonSerializer.SerializeToElement(new { });
        var requestHash = ImportJsonIdentity.Hash(JsonSerializer.SerializeToElement(new { graph = input, options }));
        await using var operationLock = await PostgresImportLock.TryAcquireAsync(db, $"task-graph-import:{operationId:D}", ct);
        if (operationLock is null) return ImportBusy(operationId);
        var op = await db.TaskGraphImportOperations.SingleOrDefaultAsync(x => x.Id == operationId, ct);
        if (op is not null)
        {
            if (op.UserId != userId || op.CourseId != courseId) return HttpResults.NotFound();
            if (op.RequestHash != requestHash) return HttpResults.Conflict(new { code = "IMPORT_OPERATION_PAYLOAD_CHANGED",
                message = "Этот operationId уже связан с другим JSON или настройками." });
            if (op.State == "stopped") return ImportStopped(op.Id);
            if (op.ResponseJson is not null) return ReplayImport(op);
        }
        else
        {
            await using var creationLock = await PostgresImportLock.TryAcquireAsync(db, $"task-graph-owner:{courseId:D}:{userId:D}", ct);
            if (creationLock is null) return ImportBusy(operationId);
            var pending = await db.TaskGraphImportOperations.AsNoTracking().Where(x => x.CourseId == courseId && x.UserId == userId
                && x.State != "completed" && x.State != "stopped").Select(x => (Guid?)x.Id).FirstOrDefaultAsync(ct);
            if (pending.HasValue) return HttpResults.Conflict(new { code = "IMPORT_PREVIOUS_PENDING", operationId = pending.Value,
                message = "Сначала завершите предыдущий импорт." });
            if (options.ValueKind == JsonValueKind.Object && options.TryGetProperty("expectedMapVersion", out var expectedMap)
                && expectedMap.ValueKind == JsonValueKind.Number && expectedMap.TryGetInt32(out var expectedVersion))
            {
                var map = await GetInternalAsync<CourseMapInternalResponse>(clients, cfg,
                    ServiceUrl(cfg, "EducationApi", "http://education-api:8080"), $"/api/internal/courses/{courseId:D}/map", ct);
                if (map is null) return HttpResults.Json(new { code = "IMPORT_MAP_CHECK_UNAVAILABLE" }, statusCode: 503);
                if (map.Version != expectedVersion) return HttpResults.Conflict(new { code = "IMPORT_PREVIEW_CHANGED", currentVersion = map.Version,
                    message = "Карта изменилась после Preview. Повторите предпросмотр." });
            }
            if (requestGraph is not null)
            {
                var tree = await GetInternalAsync<CourseTreeResponse>(clients, cfg,
                    ServiceUrl(cfg, "EducationApi", "http://education-api:8080"), $"/api/internal/courses/{courseId:D}/tree", ct);
                if (tree is null) return HttpResults.Json(new { code = "COURSE_TREE_UNAVAILABLE" }, statusCode: 503);
                var bindings = await CheckTaskGraphBindingsAsync(requestGraph, courseId, tree.CourseIds.Append(courseId).ToHashSet(), db, clients, cfg, ct);
                if (bindings.Count > 0) return HttpResults.BadRequest(new { code = "TASK_GRAPH_BINDING_INVALID", issues = bindings });
            }
            var reserved = ImportJsonIdentity.ReserveMissingIds(input, courseId);
            if (requestGraph is not null)
            {
                // Check derived identities as strictly as explicit client UUIDs.
                // A deterministic key must never hijack an unrelated subtree.
                var withIds = ParseAndValidate(reserved, ReadGraphImportOptions(payload));
                if (withIds.Graph is null || withIds.Issues.Count > 0)
                    return HttpResults.BadRequest(new { code = "TASK_GRAPH_VALIDATION_FAILED", issues = withIds.Issues });
                var tree = await GetInternalAsync<CourseTreeResponse>(clients, cfg,
                    ServiceUrl(cfg, "EducationApi", "http://education-api:8080"), $"/api/internal/courses/{courseId:D}/tree", ct);
                if (tree is null) return HttpResults.Json(new { code = "COURSE_TREE_UNAVAILABLE" }, statusCode: 503);
                var bindingIssues = await CheckTaskGraphBindingsAsync(withIds.Graph, courseId,
                    tree.CourseIds.Append(courseId).ToHashSet(), db, clients, cfg, ct);
                if (bindingIssues.Count > 0)
                    return HttpResults.BadRequest(new { code = "TASK_GRAPH_BINDING_INVALID", issues = bindingIssues });
            }
            // Only canonical graphs have the strict cross-course binding contract.
            // Legacy input keeps compatibility and its transaction receipt, without
            // claiming a content-version precondition that its shape cannot express.
            var layoutOnly = IsLayoutOnlyGraphImport(requestGraph, ReadGraphImportOptions(payload));
            LogImportPhase(http, prepareElapsed, layoutOnly ? "prepare.layout-only-snapshot-skipped" : "prepare.snapshot-start",
                requestGraph?.Tasks.Count ?? 0, requestGraph?.Courses.Count ?? 0);
            var baseline = requestGraph is null || layoutOnly
                ? null : ImportSnapshotHash(await CaptureImportAssignments(db, reserved, ct, ImportSnapshotOptions(payload)));
            LogImportPhase(http, prepareElapsed, "prepare.snapshot-ready", requestGraph?.Tasks.Count ?? 0,
                requestGraph?.Courses.Count ?? 0);
            op = new TaskGraphImportOperation { Id = operationId, UserId = userId, CourseId = courseId, RequestHash = requestHash,
                PreparedPayloadJson = JsonSerializer.Serialize(new { graph = reserved, options }, ImportJournalJson),
                AuditJson = JsonSerializer.Serialize(new { format = "taskforge-import-report", version = 1,
                    requestHash, dataBaselineSha256 = baseline, snapshotSchema = 2, scopes = input.ValueKind == JsonValueKind.Object && input.TryGetProperty("scopes", out var scopes) ? (object)scopes : null,
                    options = SafeImportOptions(options), identity = "operationId", rawPayloadIncluded = false }, ImportJournalJson) };
            db.TaskGraphImportOperations.Add(op);
            await db.SaveChangesAsync(ct);
            LogImportPhase(http, prepareElapsed, "prepare.journal-saved", requestGraph?.Tasks.Count ?? 0,
                requestGraph?.Courses.Count ?? 0);
        }
        return await ExecutePreparedImport(op, http, cfg, db, clients, ct);
    }

    private static async Task<IResult> ExecutePreparedImport(TaskGraphImportOperation op, HttpContext http,
        IConfiguration cfg, TasksDbContext db, IHttpClientFactory clients, CancellationToken ct)
    {
        if (op.State == "stopped") return ImportStopped(op.Id);
        if (op.ResponseJson is not null) return ReplayImport(op);
        http.Items[ImportOperationContextKey] = op.Id;
        var elapsed = Stopwatch.StartNew();
        var preparedPayload = ImportJsonIdentity.Read(op.PreparedPayloadJson);
        var importGraph = ReadGraphImportPayload(preparedPayload);
        var layoutOnly = IsLayoutOnlyGraphImport(preparedPayload);
        var phase = "prepare-attempt";
        LogImportPhase(http, elapsed, layoutOnly ? "attempt.layout-only" : "attempt.full-import");
        op.AttemptCount++;
        op.State = "prepared"; op.LastErrorJson = null; op.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        Console.WriteLine($"TASK_GRAPH_IMPORT_BEGIN OPERATION={op.Id:D} COURSE={op.CourseId:D} ATTEMPT={op.AttemptCount} LAYOUT_ONLY={layoutOnly}");
        IResult? failed = null;
        try
        {
            phase = "transaction-begin";
            await using (var transaction = await db.Database.BeginTransactionAsync(ct))
            {
                phase = "snapshot-before";
                LogImportPhase(http, elapsed, layoutOnly ? "snapshot.skipped" : "snapshot.before-start");
                var audit = JsonNode.Parse(op.AuditJson)!.AsObject();
                var snapshotOptions = audit["snapshotSchema"]?.GetValue<int>() == 2 ? ImportSnapshotOptions(preparedPayload) : null;
                var before = layoutOnly
                    ? new Dictionary<Guid, JsonObject>()
                    : await CaptureImportAssignments(db, importGraph, ct, snapshotOptions);
                LogImportPhase(http, elapsed, "snapshot.before-done", before.Count);
                var expectedBaseline = layoutOnly ? null : audit["dataBaselineSha256"]?.GetValue<string>();
                phase = "apply";
                LogImportPhase(http, elapsed, "apply.start");
                var result = expectedBaseline is not null && expectedBaseline != ImportSnapshotHash(before)
                    ? HttpResults.Conflict(new { code = "IMPORT_CONTENT_CHANGED",
                        message = "Содержимое заданий изменилось после начала операции. Прекратите её без отката и подготовьте новый Preview." })
                    : await ApplyTaskGraphImportAsync(op.CourseId, preparedPayload, http, cfg, db, clients, ct);
                var status = (result as IStatusCodeHttpResult)?.StatusCode ?? 200;
                LogImportPhase(http, elapsed, $"apply.returned-status-{status}");
                if (status >= 400)
                {
                    phase = "rollback";
                    await transaction.RollbackAsync(ct);
                    failed = result;
                }
                else
                {
                    if (result is not IValueHttpResult value) throw new InvalidOperationException("Import result must contain a durable receipt.");
                    var resultNode = JsonSerializer.SerializeToNode(value.Value, ImportJournalJson)!.AsObject();
                    phase = "entity-audit";
                    LogImportPhase(http, elapsed, layoutOnly ? "audit.layout-only" : "audit.full-start");
                    if (layoutOnly) AddLayoutOnlyEntityAudit(resultNode);
                    else await AddImportEntityAudit(db, before, resultNode, ct, snapshotOptions);
                    LogImportPhase(http, elapsed, "audit.done");
                    op.ResponseJson = resultNode.ToJsonString(ImportJournalJson);
                    var response = ImportJsonIdentity.Read(op.ResponseJson);
                    op.State = response.TryGetProperty("taskGraph", out var graph) && graph.ValueKind == JsonValueKind.Object
                        ? "data-committed" : "completed";
                    op.UpdatedAt = DateTimeOffset.UtcNow;
                    phase = "receipt-save";
                    await db.SaveChangesAsync(ct);
                    phase = "commit";
                    LogImportPhase(http, elapsed, "transaction.commit-start");
                    await transaction.CommitAsync(ct);
                    LogImportPhase(http, elapsed, "transaction.commit-done");
                    Console.WriteLine($"TASK_GRAPH_IMPORT_DATA_COMMIT OPERATION={op.Id:D} STATE={op.State} ELAPSED_MS={elapsed.ElapsedMilliseconds}");
                    return ReplayImport(op);
                }
            }
        }
        catch (Exception ex)
        {
            // Without a durable receipt, a failed commit acknowledgement cannot be treated as success or failure.
            Console.WriteLine($"TASK_GRAPH_IMPORT_UNCONFIRMED OPERATION={op.Id:D} PHASE={phase} " +
                $"ELAPSED_MS={elapsed.ElapsedMilliseconds} ERROR={ex.GetType().Name} " +
                $"CANCEL_REQUESTED={ct.IsCancellationRequested}");
            throw;
        }
        db.ChangeTracker.Clear();
        var stored = await db.TaskGraphImportOperations.SingleAsync(x => x.Id == op.Id, ct);
        stored.State = "data-failed";
        stored.LastErrorJson = JsonSerializer.Serialize((failed as IValueHttpResult)?.Value ?? new { code = "IMPORT_DATA_FAILED" }, ImportJournalJson);
        stored.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        Console.WriteLine($"TASK_GRAPH_IMPORT_DATA_REJECT OPERATION={op.Id:D} ELAPSED_MS={elapsed.ElapsedMilliseconds}");
        return failed!;
    }

    private static void MapTaskGraphOperationEndpoints(WebApplication app)
    {
        app.MapGet("/api/courses/{courseId:guid}/assignments/import-json/operations", async (Guid courseId, HttpContext http,
            IConfiguration cfg, TasksDbContext db, IHttpClientFactory clients, CancellationToken ct) =>
        {
            if (!await CanReadImportJournal(courseId, http, cfg, clients, ct)) return CourseEditForbidden();
            var user = RequireUser(http, cfg);
            var rows = await db.TaskGraphImportOperations.AsNoTracking().Where(x => x.CourseId == courseId && x.UserId == user)
                .OrderByDescending(x => x.UpdatedAt).Take(30)
                .Select(x => new { operationId = x.Id, x.State, x.CreatedAt, x.UpdatedAt }).ToListAsync(ct);
            http.Response.Headers.CacheControl = "no-store";
            return HttpResults.Ok(rows);
        });
        app.MapGet("/api/courses/{courseId:guid}/assignments/import-json/operations/{operationId:guid}", async (Guid courseId, Guid operationId,
            HttpContext http, IConfiguration cfg, TasksDbContext db, IHttpClientFactory clients, CancellationToken ct) =>
        {
            if (!await CanReadImportJournal(courseId, http, cfg, clients, ct)) return CourseEditForbidden();
            var user = RequireUser(http, cfg);
            var op = await db.TaskGraphImportOperations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == operationId && x.CourseId == courseId && x.UserId == user, ct);
            http.Response.Headers.CacheControl = "no-store";
            return op is null ? HttpResults.NotFound() : HttpResults.Ok(ImportOperationView(op));
        });
        app.MapPost("/api/courses/{courseId:guid}/assignments/import-json/operations/{operationId:guid}/resume", ResumeTaskGraphAsync).AddEndpointFilter<SqlEndpointFilter>();
        app.MapPost("/api/courses/{courseId:guid}/assignments/import-json/operations/{operationId:guid}/stop", async (Guid courseId, Guid operationId,
            JsonElement payload, HttpContext http, IConfiguration cfg, TasksDbContext db, IHttpClientFactory clients, CancellationToken ct) =>
        {
            if (!await CanReadImportJournal(courseId, http, cfg, clients, ct)) return CourseEditForbidden();
            if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty("acknowledgePartial", out var confirmed)
                || confirmed.ValueKind != JsonValueKind.True) return HttpResults.BadRequest(new { code = "IMPORT_STOP_CONFIRM_REQUIRED" });
            await using var operationLock = await PostgresImportLock.TryAcquireAsync(db, $"task-graph-import:{operationId:D}", ct);
            if (operationLock is null) return ImportBusy(operationId);
            var user = RequireUser(http, cfg);
            var op = await db.TaskGraphImportOperations.SingleOrDefaultAsync(x => x.Id == operationId && x.CourseId == courseId && x.UserId == user, ct);
            if (op is null) return HttpResults.NotFound();
            if (op.State == "map-pending") return HttpResults.Conflict(new { code = "IMPORT_MAP_UNCONFIRMED", operationId,
                message = "Сначала подтвердите результат сохранения карты через продолжение этой операции." });
            if (op.State != "completed") { op.State = "stopped"; op.UpdatedAt = DateTimeOffset.UtcNow; await db.SaveChangesAsync(ct); }
            Console.WriteLine($"TASK_GRAPH_IMPORT_STOP OPERATION={op.Id:D} STATE={op.State} ROLLBACK=false");
            return HttpResults.Ok(ImportOperationView(op));
        });
        app.MapPut("/api/courses/{courseId:guid}/assignments/import-json/operations/{operationId:guid}/map", SaveTaskGraphMapAsync).AddEndpointFilter<SqlEndpointFilter>();
    }

    private static async Task<IResult> SendJournaledMap(TaskGraphImportOperation op, HttpContext http, IConfiguration cfg,
        TasksDbContext db, IHttpClientFactory clients, CancellationToken ct)
    {
        if (op.MapResponseJson is not null) return HttpResults.Json(ImportJsonIdentity.Read(op.MapResponseJson));
        http.Items[ImportOperationContextKey] = op.Id;
        var mapElapsed = Stopwatch.StartNew();
        LogImportPhase(http, mapElapsed, "map.begin");
        var background = GraphTransferActor.Get(http) is not null;
        using var request = new HttpRequestMessage(HttpMethod.Put,
            ServiceUrl(cfg, "EducationApi", "http://education-api:8080").TrimEnd('/')
            + (background ? $"/api/internal/courses/{op.CourseId:D}/map/transfer" : $"/api/courses/{op.CourseId:D}/map"));
        if (background)
        {
            GraphTransferActor.AddInternalKey(request, cfg);
            request.Content = JsonContent.Create(new { userId = op.UserId, request = ImportJsonIdentity.Read(op.MapRequestJson!) });
        }
        else
        {
            var bearer = http.Request.Headers.Authorization.ToString();
            if (!string.IsNullOrWhiteSpace(bearer)) request.Headers.TryAddWithoutValidation("Authorization", bearer);
            else if (http.Request.Cookies.TryGetValue("tf_at", out var token)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            else return HttpResults.Unauthorized();
            request.Content = new StringContent(op.MapRequestJson!, System.Text.Encoding.UTF8, "application/json");
        }
        Console.WriteLine($"TASK_GRAPH_IMPORT_MAP_BEGIN OPERATION={op.Id:D}");
        HttpResponseMessage httpResponse;
        try
        {
            httpResponse = await clients.CreateClient().SendAsync(request, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            LogImportPhase(http, mapElapsed, "map.http-cancelled");
            throw;
        }
        using var response = httpResponse;
        LogImportPhase(http, mapElapsed, $"map.http-status-{(int)response.StatusCode}");
        var text = await response.Content.ReadAsStringAsync(ct);
        LogImportPhase(http, mapElapsed, "map.response-body-read");
        JsonElement result;
        try { result = ImportJsonIdentity.Read(text); }
        catch (JsonException) { return HttpResults.Json(new { code = "IMPORT_MAP_UNCONFIRMED", operationId = op.Id,
            message = "Результат сохранения карты не подтверждён. Повторите эту операцию." }, statusCode: 502); }
        if (!response.IsSuccessStatusCode)
        {
            op.LastErrorJson = result.GetRawText();
            op.State = response.StatusCode == System.Net.HttpStatusCode.Conflict && result.ValueKind == JsonValueKind.Object && result.TryGetProperty("code", out var code)
                && code.GetString() == "COURSE_MAP_VERSION_CONFLICT" ? "map-conflict"
                : response.StatusCode == System.Net.HttpStatusCode.BadRequest ? "map-rejected" : "map-pending";
            op.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            Console.WriteLine($"TASK_GRAPH_IMPORT_MAP_REJECT OPERATION={op.Id:D} STATUS={(int)response.StatusCode} STATE={op.State}");
            return HttpResults.Json(result, statusCode: (int)response.StatusCode);
        }
        // A 2xx from the remote service is insufficient proof of a correct map.
        // Its transaction receipt must contain the exact submitted document.
        var durable = ImportJsonIdentity.Read(op.MapRequestJson!);
        var expectedVersion = durable.GetProperty("expectedVersion").GetInt32();
        LogImportPhase(http, mapElapsed, "map.receipt-validate");
        if (!TaskGraphMapIntegrity.MatchesSavedDocument(durable, result, expectedVersion))
        {
            op.State = "map-rejected";
            op.LastErrorJson = JsonSerializer.Serialize(new { code = "IMPORT_MAP_RECEIPT_MISMATCH" }, ImportJournalJson);
            op.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            return HttpResults.Json(new { code = "IMPORT_MAP_RECEIPT_MISMATCH", operationId = op.Id,
                message = "Ответ сервера не подтверждает сохранение всех данных карты. Повторите проверку карты." }, statusCode: 502);
        }
        var audit = JsonNode.Parse(op.AuditJson)!.AsObject();
        if (audit["mapBefore"] is JsonObject baseline && result.ValueKind == JsonValueKind.Object
            && result.TryGetProperty("version", out var savedVersion) && savedVersion.TryGetInt32(out var version))
        {
            var after = MapImportAuditState(result.TryGetProperty("document", out var savedMap) ? savedMap : null, version);
            audit["mapDiff"] = MapImportAuditDiff(baseline, after);
            op.AuditJson = audit.ToJsonString(ImportJournalJson);
        }
        op.MapResponseJson = result.GetRawText(); op.State = "completed"; op.LastErrorJson = null; op.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        LogImportPhase(http, mapElapsed, "map.journal-complete");
        Console.WriteLine($"TASK_GRAPH_IMPORT_END OPERATION={op.Id:D} STATE=completed MAP_ELAPSED_MS={mapElapsed.ElapsedMilliseconds}");
        return HttpResults.Json(result);
    }

    private static async Task<IResult> ResumeTaskGraphAsync(Guid courseId, Guid operationId, HttpContext http, IConfiguration cfg, TasksDbContext db, IHttpClientFactory clients, CancellationToken ct)
    {
            if (!await CanReadImportJournal(courseId, http, cfg, clients, ct)) return CourseEditForbidden();
            await using var operationLock = await PostgresImportLock.TryAcquireAsync(db, $"task-graph-import:{operationId:D}", ct);
            if (operationLock is null) return ImportBusy(operationId);
            var user = RequireUser(http, cfg);
            var op = await db.TaskGraphImportOperations.SingleOrDefaultAsync(x => x.Id == operationId && x.CourseId == courseId && x.UserId == user, ct);
            if (op is null) return HttpResults.NotFound();
            if (op.State == "stopped") return ImportStopped(op.Id);
            if (op.MapRequestJson is not null) return await SendJournaledMap(op, http, cfg, db, clients, ct);
            return await ExecutePreparedImport(op, http, cfg, db, clients, ct);
    }

    private static async Task<IResult> SaveTaskGraphMapAsync(Guid courseId, Guid operationId, JsonElement payload, HttpContext http, IConfiguration cfg, TasksDbContext db, IHttpClientFactory clients, CancellationToken ct)
    {
            if (!await CanReadImportJournal(courseId, http, cfg, clients, ct)) return CourseEditForbidden();
            await using var operationLock = await PostgresImportLock.TryAcquireAsync(db, $"task-graph-import:{operationId:D}", ct);
            if (operationLock is null) return ImportBusy(operationId);
            var user = RequireUser(http, cfg);
            var op = await db.TaskGraphImportOperations.SingleOrDefaultAsync(x => x.Id == operationId && x.CourseId == courseId && x.UserId == user, ct);
            if (op is null) return HttpResults.NotFound();
            if (op.State == "stopped") return ImportStopped(op.Id);
            if (op.ResponseJson is null) return HttpResults.Conflict(new { code = "IMPORT_DATA_NOT_COMMITTED" });
            if (op.MapResponseJson is not null) return HttpResults.Json(ImportJsonIdentity.Read(op.MapResponseJson));
            if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty("expectedVersion", out var version)
                || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var expected) || expected < 0
                || !payload.TryGetProperty("document", out var document) || document.ValueKind != JsonValueKind.Object)
                return HttpResults.BadRequest(new { code = "IMPORT_MAP_INVALID" });
            // The write must include every requested edge. A missing edge is not
            // a successful partial import, even when tasks were already committed.
            var missingEdges = TaskGraphMapIntegrity.MissingConnections(
                ImportJsonIdentity.Read(op.ResponseJson), document, courseId);
            if (missingEdges.Count > 0)
                return HttpResults.Conflict(new { code = "IMPORT_MAP_INCOMPLETE", operationId,
                    message = "На карте отсутствуют связи из импортированного JSON. Применение не подтверждено.", missingEdges });
            var replace = payload.TryGetProperty("replaceConflicted", out var reset) && reset.ValueKind == JsonValueKind.True;
            if (op.MapRequestJson is null || replace && (op.State == "map-conflict" || op.State == "map-rejected"))
            {
                var baseline = await GetInternalAsync<CourseMapInternalResponse>(clients, cfg,
                    ServiceUrl(cfg, "EducationApi", "http://education-api:8080"), $"/api/internal/courses/{courseId:D}/map", ct);
                if (baseline is null) return HttpResults.Json(new { code = "IMPORT_MAP_CHECK_UNAVAILABLE" }, statusCode: 503);
                var audit = JsonNode.Parse(op.AuditJson)!.AsObject();
                audit["mapBefore"] = MapImportAuditState(baseline.Document, baseline.Version);
                op.AuditJson = audit.ToJsonString(ImportJournalJson);
                op.MapRequestJson = JsonSerializer.Serialize(new { expectedVersion = expected, document, operationId = Guid.NewGuid() }, ImportJournalJson);
                op.State = "map-pending"; op.LastErrorJson = null; op.UpdatedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(ct);
            }
            // An unconfirmed write always retries the exact durable checkpoint.
            // A new graph is accepted only after a definite version conflict and
            // an explicit new preview, never as a timeout recovery heuristic.
            return await SendJournaledMap(op, http, cfg, db, clients, ct);
    }
}
