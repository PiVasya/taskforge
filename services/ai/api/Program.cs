using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using TaskForge.Ai.Api.Data;
using TaskForge.Ai.Api.Domain;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddTaskForgeDebugDiagnostics("ai-api");
builder.Services.AddTaskForgeRedisCache(builder.Configuration, "ai-api");

builder.Services.AddHealthChecks();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddSignalR();
builder.Services.AddHttpClient();
builder.Services.AddDbContext<AiDbContext>(options =>
{
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection"));
});

var app = builder.Build();

app.UseTaskForgeDebugRequestLogging("ai-api");

if (builder.Configuration.GetValue("Database:MigrateOnStartup", true))
{
    using var migrationScope = app.Services.CreateScope();
    var db = migrationScope.ServiceProvider.GetRequiredService<AiDbContext>();
    app.Logger.LogInformation("Applying EF Core migrations for AiDbContext...");
    await db.Database.MigrateAsync();
    app.Logger.LogInformation("EF Core migrations for AiDbContext applied.");
}
else if (builder.Configuration.GetValue("Database:EnsureCreated", false))
{
    using var ensureScope = app.Services.CreateScope();
    var db = ensureScope.ServiceProvider.GetRequiredService<AiDbContext>();
    await db.Database.EnsureCreatedAsync();
}


if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseTaskForgeRequestSecurity("ai");

app.MapGet("/health/live", () => Results.Ok(new { status = "ok", service = "taskforge-ai-api" }));
app.MapGet("/health/ready", async (AiDbContext db) =>
{
    var canConnect = await db.Database.CanConnectAsync();
    return canConnect ? Results.Ok(new { status = "ready", service = "taskforge-ai-api" }) : Results.StatusCode(503);
});
app.MapGet("/", () => Results.Ok(new
{
    service = "taskforge-ai-api",
    database = "taskforge_ai",
    migrations = "tracked EF Core migrations",
    status = "microservice boundary extracted"
}));
app.MapGet("/api/ai/api/schema-owner", () => Results.Ok(new
{
    database = "taskforge_ai",
    ownedEntities = new[] { "AgentConversation", "AgentMessage", "AgentRun", "AgentStep", "AgentRunArtifact", "PromptTemplate" }
}));


app.MapGet("/api/agent/conversations", async (HttpContext http, IConfiguration cfg, AiDbContext db, Guid? courseId, Guid? assignmentId) =>
{
    var uid = CurrentUserId(http, cfg);
    if (uid == null) return Results.Unauthorized();
    var q = db.Conversations.AsNoTracking();
    if (!IsEditorOrAdmin(http)) q = q.Where(x => x.UserId == uid.Value);
    if (courseId.HasValue) q = q.Where(x => x.CourseId == courseId.Value);
    if (assignmentId.HasValue) q = q.Where(x => x.AssignmentId == assignmentId.Value);
    var rows = await q.OrderByDescending(x => x.UpdatedAtUtc).Take(100).ToListAsync();
    return Results.Ok(rows.Select(ToConversationDto).ToList());
});

app.MapPost("/api/agent/conversations", async (JsonElement payload, HttpContext http, IConfiguration cfg, AiDbContext db, CancellationToken ct) =>
{
    var uid = CurrentUserId(http, cfg);
    if (uid == null) return Results.Unauthorized();
    var title = payload.TryGetProperty("title", out var t) ? t.GetString() : null;
    var c = new AiConversation
    {
        Title = string.IsNullOrWhiteSpace(title) ? "Новый диалог" : title!.Trim(),
        UserId = uid.Value,
        CourseId = GuidFromPayload(payload, "courseId"),
        AssignmentId = GuidFromPayload(payload, "assignmentId"),
        SupportTicketId = GuidFromPayload(payload, "supportTicketId")
    };
    db.Conversations.Add(c);
    await db.SaveChangesAsync(ct);
    return Results.Ok(new { conversation = ToConversationDto(c) });
});

app.MapGet("/api/agent/conversations/{conversationId:guid}", async (Guid conversationId, HttpContext http, IConfiguration cfg, AiDbContext db, CancellationToken ct) =>
{
    var c = await GetConversationForUser(conversationId, http, cfg, db, asNoTracking: true);
    if (c == null) return Results.NotFound();
    var messageRows = await db.Messages.AsNoTracking().Where(x => x.ConversationId == conversationId).OrderBy(x => x.CreatedAtUtc).ToListAsync(ct);
    var runRows = await db.Runs.AsNoTracking().Where(x => x.ConversationId == conversationId).OrderByDescending(x => x.CreatedAtUtc).ToListAsync(ct);
    var stepRows = await db.Steps.AsNoTracking().Where(x => x.ConversationId == conversationId).OrderBy(x => x.Seq).ThenBy(x => x.CreatedAtUtc).ToListAsync(ct);
    var artifactRows = await db.Artifacts.AsNoTracking().Where(x => x.ConversationId == conversationId).OrderBy(x => x.CreatedAtUtc).ToListAsync(ct);
    var messages = messageRows.Select(m => ToMessageDto(m, m.RunId.HasValue ? artifactRows.Where(a => a.RunId == m.RunId.Value).ToList() : null)).ToList();
    var runs = runRows.Select(r => ToRunDto(r, artifactRows.Where(a => a.RunId == r.Id).ToList(), stepRows.Where(st => st.RunId == r.Id).ToList())).ToList();
    return Results.Ok(new { conversation = ToConversationDto(c), messages, runs, artifacts = artifactRows.Select(ToArtifactDto).ToList() });
});

app.MapPost("/api/agent/conversations/{conversationId:guid}/messages", async (Guid conversationId, JsonElement payload, HttpRequest request, HttpContext http, IConfiguration cfg, AiDbContext db, IHttpClientFactory factory, IHubContext<AgentRealtimeHub> hub, CancellationToken ct) =>
{
    var c = await GetConversationForUser(conversationId, http, cfg, db, asNoTracking: false);
    if (c == null) return Results.NotFound(new { message = "Диалог не найден.", code = "AI_CONVERSATION_NOT_FOUND" });

    var text = ReadMessageText(payload);
    var clientId = payload.TryGetProperty("clientMessageId", out var cmid) ? cmid.GetString() : null;
    var userMessage = new AiMessage { ConversationId = conversationId, Role = "user", Content = text, ClientMessageId = clientId };
    db.Messages.Add(userMessage);
    c.UpdatedAtUtc = DateTimeOffset.UtcNow;
    await db.SaveChangesAsync(ct);

    var runPayload = await BuildRunPayloadAsync(c, payload, text, request, db, factory, ct);
    var run = new AiRun
    {
        ConversationId = conversationId,
        Status = "queued",
        JobType = "assistant_chat_turn",
        PayloadJson = runPayload.ToJsonString(JsonOptions())
    };
    db.Runs.Add(run);
    c.UpdatedAtUtc = DateTimeOffset.UtcNow;
    await db.SaveChangesAsync(ct);

    var messageDto = ToMessageDto(userMessage);
    var runDto = ToRunDto(run);
    await BroadcastAgentEventAsync(hub, conversationId, "message.created", new { message = messageDto }, ct);
    await BroadcastAgentEventAsync(hub, conversationId, "run.created", new { run = runDto }, ct);
    return Results.Ok(new { conversation = ToConversationDto(c), message = messageDto, run = runDto, queued = true });
});

app.MapPost("/api/agent/conversations/{conversationId:guid}/attachments", async (Guid conversationId, HttpRequest request, HttpContext http, IConfiguration cfg, AiDbContext db, CancellationToken ct) => await SaveAttachment(conversationId, request, http, cfg, db, ct)).DisableAntiforgery();
app.MapPost("/api/agent/conversations/{conversationId:guid}/polish-task", async (Guid conversationId, JsonElement payload, HttpContext http, IConfiguration cfg, AiDbContext db, IHubContext<AgentRealtimeHub> hub, CancellationToken ct) => await QueueAgentRun(conversationId, "polish_assignment_draft", payload, http, cfg, db, hub, ct));
app.MapPost("/api/agent/conversations/{conversationId:guid}/polish-tasks", async (Guid conversationId, JsonElement payload, HttpContext http, IConfiguration cfg, AiDbContext db, IHubContext<AgentRealtimeHub> hub, CancellationToken ct) => await QueueAgentRun(conversationId, "polish_assignment_draft", payload, http, cfg, db, hub, ct));
app.MapPost("/api/agent/runs/{runId:guid}/cancel", async (Guid runId, HttpContext http, IConfiguration cfg, AiDbContext db, IHubContext<AgentRealtimeHub> hub, CancellationToken ct) =>
{
    var run = await db.Runs.FindAsync(new object?[] { runId }, ct);
    if (run == null) return Results.NotFound(new { message = "Обработка не найдена.", code = "AI_RUN_NOT_FOUND" });
    var c = await GetConversationForUser(run.ConversationId, http, cfg, db, asNoTracking: true);
    if (c == null) return Results.Json(new { message = "Нет доступа к этой обработке.", code = "AI_RUN_FORBIDDEN" }, statusCode: 403);
    run.Status = "canceled";
    run.UpdatedAtUtc = DateTimeOffset.UtcNow;
    await db.SaveChangesAsync(ct);
    await BroadcastAgentEventAsync(hub, run.ConversationId, "run.updated", new { run = ToRunDto(run), runId, status = "canceled" }, ct);
    return Results.Ok(new { runId, status = "canceled" });
});
app.MapPost("/api/agent/artifacts/{artifactId:guid}/apply", async (Guid artifactId, JsonElement payload, HttpRequest request, HttpContext http, IConfiguration cfg, AiDbContext db, IHttpClientFactory factory, CancellationToken ct) => await ApplyArtifact(null, artifactId, payload, request, http, cfg, db, factory, ct));
app.MapPost("/api/agent/runs/{runId:guid}/artifacts/{artifactId:guid}/apply", async (Guid runId, Guid artifactId, JsonElement payload, HttpRequest request, HttpContext http, IConfiguration cfg, AiDbContext db, IHttpClientFactory factory, CancellationToken ct) => await ApplyArtifact(runId, artifactId, payload, request, http, cfg, db, factory, ct));
app.MapGet("/api/agent/conversations/{conversationId:guid}/debug-dump", async (Guid conversationId, HttpContext http, IConfiguration cfg, AiDbContext db, CancellationToken ct) =>
{
    var c = await GetConversationForUser(conversationId, http, cfg, db, asNoTracking: true);
    if (c == null) return Results.NotFound(new { message = "Диалог не найден.", code = "AI_CONVERSATION_NOT_FOUND" });

    var messages = await db.Messages.AsNoTracking().Where(x => x.ConversationId == conversationId).OrderBy(x => x.CreatedAtUtc).ToListAsync(ct);
    var runs = await db.Runs.AsNoTracking().Where(x => x.ConversationId == conversationId).OrderBy(x => x.CreatedAtUtc).ToListAsync(ct);
    var steps = await db.Steps.AsNoTracking().Where(x => x.ConversationId == conversationId).OrderBy(x => x.CreatedAtUtc).ThenBy(x => x.Seq).ToListAsync(ct);
    var artifacts = await db.Artifacts.AsNoTracking().Where(x => x.ConversationId == conversationId).OrderBy(x => x.CreatedAtUtc).ToListAsync(ct);

    var sb = new StringBuilder();
    sb.AppendLine("# TaskForge AI trace export");
    sb.AppendLine();
    sb.AppendLine($"generatedAtUtc: {DateTimeOffset.UtcNow:O}");
    sb.AppendLine($"conversationId: {conversationId}");
    sb.AppendLine($"title: {c.Title}");
    sb.AppendLine($"courseId: {c.CourseId?.ToString() ?? "null"}");
    sb.AppendLine($"assignmentId: {c.AssignmentId?.ToString() ?? "null"}");
    sb.AppendLine();

    sb.AppendLine("## Messages");
    foreach (var m in messages)
    {
        sb.AppendLine($"### [{m.CreatedAtUtc:O}] {m.Role} message={m.Id} run={m.RunId?.ToString() ?? "null"}");
        sb.AppendLine(m.Content ?? string.Empty);
        sb.AppendLine();
    }

    sb.AppendLine("## Runs and steps");
    foreach (var r in runs)
    {
        sb.AppendLine($"### Run {r.Id}");
        sb.AppendLine($"status: {r.Status}");
        sb.AppendLine($"jobType: {r.JobType}");
        sb.AppendLine($"workerId: {r.WorkerId ?? "null"}");
        sb.AppendLine($"createdAtUtc: {r.CreatedAtUtc:O}");
        sb.AppendLine($"startedAtUtc: {r.StartedAtUtc?.ToString("O") ?? "null"}");
        sb.AppendLine($"completedAtUtc: {r.CompletedAtUtc?.ToString("O") ?? "null"}");
        if (!string.IsNullOrWhiteSpace(r.ErrorJson))
        {
            sb.AppendLine("errorJson:");
            sb.AppendLine(r.ErrorJson);
        }
        sb.AppendLine("payloadJson:");
        sb.AppendLine(r.PayloadJson);
        sb.AppendLine();

        foreach (var st in steps.Where(x => x.RunId == r.Id).OrderBy(x => x.Seq))
        {
            sb.AppendLine($"#### Step {st.Seq}: {st.Title}");
            sb.AppendLine($"kind: {st.Kind}");
            sb.AppendLine($"actionName: {st.ActionName}");
            sb.AppendLine($"status: {st.Status}");
            sb.AppendLine($"createdAtUtc: {st.CreatedAtUtc:O}");
            if (!string.IsNullOrWhiteSpace(st.Summary)) sb.AppendLine($"summary: {st.Summary}");
            if (!string.IsNullOrWhiteSpace(st.DataJson))
            {
                sb.AppendLine("dataJson:");
                sb.AppendLine(st.DataJson);
            }
            sb.AppendLine();
        }
    }

    sb.AppendLine("## Artifacts");
    foreach (var a in artifacts)
    {
        sb.AppendLine($"### Artifact {a.Id}");
        sb.AppendLine($"runId: {a.RunId}");
        sb.AppendLine($"type: {a.Type}");
        sb.AppendLine($"title: {a.Title}");
        sb.AppendLine($"createdAtUtc: {a.CreatedAtUtc:O}");
        sb.AppendLine("dataJson:");
        sb.AppendLine(a.DataJson);
        sb.AppendLine();
    }

    return Results.Text(sb.ToString(), "text/plain");
});
app.MapHub<AgentRealtimeHub>("/hubs/agent");

app.MapPost("/api/internal/agent/claim-next", async (AgentClaimNextRequest request, AiDbContext db, IHubContext<AgentRealtimeHub> hub, CancellationToken ct) =>
{
    var run = await db.Runs.Where(x => x.Status == "queued").OrderBy(x => x.CreatedAtUtc).FirstOrDefaultAsync(ct);
    if (run == null) return Results.Ok(new { ok = true, request.WorkerId, job = (object?)null });
    run.Status = "running";
    run.WorkerId = request.WorkerId;
    run.StartedAtUtc = DateTimeOffset.UtcNow;
    run.UpdatedAtUtc = DateTimeOffset.UtcNow;
    await db.SaveChangesAsync(ct);
    await BroadcastAgentEventAsync(hub, run.ConversationId, "run.updated", new { run = ToRunDto(run), runId = run.Id, status = run.Status, workerId = request.WorkerId }, ct);
    var payload = ParseJson(run.PayloadJson) ?? new { conversationId = run.ConversationId, rawText = "" };
    return Results.Ok(new { ok = true, request.WorkerId, job = new { id = run.Id, runId = run.Id, run.ConversationId, jobType = run.JobType, type = run.JobType, payload } });
});

app.MapPost("/api/internal/agent/runs/{runId:guid}/heartbeat", async (Guid runId, AgentWorkerRequest request, AiDbContext db, CancellationToken ct) =>
{
    var run = await db.Runs.FindAsync(new object?[] { runId }, ct);
    if (run != null)
    {
        run.WorkerId = request.WorkerId;
        run.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }
    return Results.Ok(new { ok = true, runId, request.WorkerId });
});

app.MapPost("/api/internal/agent/runs/{runId:guid}/steps", async (Guid runId, AgentStepRequest request, AiDbContext db, IHubContext<AgentRealtimeHub> hub, CancellationToken ct) =>
{
    var run = await db.Runs.FindAsync(new object?[] { runId }, ct);
    if (run == null) return Results.NotFound(new { message = "Обработка не найдена.", code = "AI_RUN_NOT_FOUND" });

    var nextSeq = (await db.Steps.Where(x => x.RunId == runId).Select(x => (int?)x.Seq).MaxAsync(ct) ?? 0) + 1;
    var step = BuildStep(run, request, nextSeq);
    db.Steps.Add(step);
    run.UpdatedAtUtc = DateTimeOffset.UtcNow;
    await db.SaveChangesAsync(ct);

    var stepDto = ToStepDto(step);
    await BroadcastAgentEventAsync(hub, run.ConversationId, "step.created", new { runId, step = stepDto }, ct);
    return Results.Ok(new { ok = true, runId, request.WorkerId, step = stepDto });
});

app.MapPost("/api/internal/agent/runs/{runId:guid}/complete", async (Guid runId, AgentCompleteRequest request, AiDbContext db, IHubContext<AgentRealtimeHub> hub, CancellationToken ct) =>
{
    var run = await db.Runs.FindAsync(new object?[] { runId }, ct);
    if (run == null) return Results.NotFound(new { message = "Обработка не найдена.", code = "AI_RUN_NOT_FOUND" });
    run.Status = "completed";
    run.WorkerId = request.WorkerId;
    run.CompletedAtUtc = DateTimeOffset.UtcNow;
    run.UpdatedAtUtc = DateTimeOffset.UtcNow;
    var text = ExtractAssistantMessage(request.Result);
    var assistantMessage = new AiMessage { ConversationId = run.ConversationId, RunId = run.Id, Role = "assistant", Content = text };
    var artifacts = ExtractArtifacts(run.Id, run.ConversationId, request.Result).ToList();
    db.Messages.Add(assistantMessage);
    db.Artifacts.AddRange(artifacts);
    var c = await db.Conversations.FindAsync(new object?[] { run.ConversationId }, ct);
    if (c != null) c.UpdatedAtUtc = DateTimeOffset.UtcNow;
    await db.SaveChangesAsync(ct);

    var runDto = ToRunDto(run, artifacts, await db.Steps.AsNoTracking().Where(x => x.RunId == runId).OrderBy(x => x.Seq).ToListAsync(ct));
    await BroadcastAgentEventAsync(hub, run.ConversationId, "message.created", new { message = ToMessageDto(assistantMessage, artifacts) }, ct);
    await BroadcastAgentEventAsync(hub, run.ConversationId, "run.completed", new { runId, status = "completed", result = request.Result, run = runDto }, ct);
    return Results.Ok(new { ok = true, runId, request.WorkerId, status = "completed" });
});

app.MapPost("/api/internal/agent/runs/{runId:guid}/fail", async (Guid runId, AgentFailRequest request, AiDbContext db, IHubContext<AgentRealtimeHub> hub, CancellationToken ct) =>
{
    var run = await db.Runs.FindAsync(new object?[] { runId }, ct);
    if (run != null)
    {
        run.Status = "failed";
        run.WorkerId = request.WorkerId;
        run.ErrorJson = request.Error?.GetRawText();
        run.CompletedAtUtc = DateTimeOffset.UtcNow;
        run.UpdatedAtUtc = DateTimeOffset.UtcNow;
        var message = new AiMessage { ConversationId = run.ConversationId, RunId = run.Id, Role = "assistant", Content = "Ассистент не смог завершить задачу. Проверьте настройки провайдера модели." };
        db.Messages.Add(message);
        await db.SaveChangesAsync(ct);
        await BroadcastAgentEventAsync(hub, run.ConversationId, "message.created", new { message = ToMessageDto(message) }, ct);
        await BroadcastAgentEventAsync(hub, run.ConversationId, "run.failed", new { runId, status = "failed", error = request.Error, run = ToRunDto(run) }, ct);
    }
    return Results.Ok(new { ok = true, runId, request.WorkerId, status = "failed" });
});

app.MapPost("/api/internal/agent/tools/run-tests", async (AgentRunTestsRequest request, IHttpClientFactory factory, IConfiguration cfg, CancellationToken ct) => await RunTestsBridge(request, factory, cfg, ct));


static Guid? CurrentUserId(HttpContext http, IConfiguration cfg) => TaskForgeRequestSecurity.UserId(http, cfg);

static bool IsEditorOrAdmin(HttpContext http)
    => http.User?.Identity?.IsAuthenticated == true && TaskForgeRequestSecurity.HasAnyRole(http.User, "Admin", "Editor", "LearningEditor");

static async Task<TaskForge.Ai.Api.Domain.AiConversation?> GetConversationForUser(Guid conversationId, HttpContext http, IConfiguration cfg, AiDbContext db, bool asNoTracking)
{
    var uid = CurrentUserId(http, cfg);
    if (uid == null) return null;

    var q = asNoTracking ? db.Conversations.AsNoTracking() : db.Conversations.AsQueryable();
    var conversation = await q.FirstOrDefaultAsync(x => x.Id == conversationId);
    if (conversation == null) return null;

    // Old orphan conversations must not be auto-adopted by the first user who knows the id.
    // Admin/editor can inspect them; normal users get no access until a migration assigns ownership.
    if (conversation.UserId == null)
    {
        return IsEditorOrAdmin(http) ? conversation : null;
    }

    if (conversation.UserId == uid.Value || IsEditorOrAdmin(http)) return conversation;
    return null;
}

static async Task<IResult> SaveAttachment(Guid conversationId, HttpRequest request, HttpContext http, IConfiguration cfg, AiDbContext db, CancellationToken ct)
{
    var c = await GetConversationForUser(conversationId, http, cfg, db, asNoTracking: false);
    if (c == null) return Results.NotFound(new { message = "Диалог не найден.", code = "AI_CONVERSATION_NOT_FOUND" });
    var form = await request.ReadFormAsync(ct);
    var file = form.Files.FirstOrDefault();
    if (file == null || file.Length == 0) return Results.BadRequest(new { message = "Файл не передан.", code = "AI_ATTACHMENT_REQUIRED" });
    if (file.Length > 8 * 1024 * 1024) return Results.Json(new { message = "Файл слишком большой для AI-вложения. Максимум 8 MB.", code = "AI_ATTACHMENT_TOO_LARGE" }, statusCode: 413);
    await using var ms = new MemoryStream();
    await file.CopyToAsync(ms, ct);
    var node = new JsonObject
    {
        ["fileName"] = file.FileName,
        ["contentType"] = string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType,
        ["size"] = file.Length,
        ["base64"] = Convert.ToBase64String(ms.ToArray())
    };
    var msg = new TaskForge.Ai.Api.Domain.AiMessage { ConversationId = conversationId, Role = "attachment", Content = node.ToJsonString() };
    db.Messages.Add(msg);
    c.UpdatedAtUtc = DateTimeOffset.UtcNow;
    await db.SaveChangesAsync(ct);
    return Results.Ok(new { attachment = ToMessageDto(msg), queued = false });
}

static async Task<IResult> QueueAgentRun(Guid conversationId, string jobType, JsonElement payload, HttpContext http, IConfiguration cfg, AiDbContext db, IHubContext<AgentRealtimeHub> hub, CancellationToken ct)
{
    var c = await GetConversationForUser(conversationId, http, cfg, db, asNoTracking: false);
    if (c == null) return Results.NotFound(new { message = "Диалог не найден.", code = "AI_CONVERSATION_NOT_FOUND" });
    var root = payload.ValueKind == JsonValueKind.Object ? JsonNode.Parse(payload.GetRawText()) as JsonObject ?? new JsonObject() : new JsonObject();
    if (root["request"] == null) root["request"] = root.DeepClone();
    root["conversationId"] = conversationId.ToString();
    if (root["courseId"] == null && c.CourseId.HasValue) root["courseId"] = c.CourseId.Value.ToString();
    if (root["assignmentId"] == null && c.AssignmentId.HasValue) root["assignmentId"] = c.AssignmentId.Value.ToString();
    if (root["action"] == null) root["action"] = jobType;
    var run = new TaskForge.Ai.Api.Domain.AiRun
    {
        ConversationId = conversationId,
        Status = "queued",
        JobType = jobType,
        PayloadJson = root.ToJsonString()
    };
    c.UpdatedAtUtc = DateTimeOffset.UtcNow;
    db.Runs.Add(run);
    await db.SaveChangesAsync(ct);
    var runDto = ToRunDto(run);
    await BroadcastAgentEventAsync(hub, conversationId, "run.created", new { run = runDto }, ct);
    return Results.Ok(new { run = runDto, queued = true });
}

static async Task<IResult> ApplyArtifact(Guid? runId, Guid artifactId, JsonElement payload, HttpRequest request, HttpContext http, IConfiguration cfg, AiDbContext db, IHttpClientFactory factory, CancellationToken ct)
{
    var artifact = await db.Artifacts.FirstOrDefaultAsync(x => x.Id == artifactId && (!runId.HasValue || x.RunId == runId.Value), ct);
    if (artifact == null) return Results.NotFound(new { message = "Материал ассистента не найден. Обновите диалог и попробуйте снова.", code = "AI_ARTIFACT_NOT_FOUND" });
    var c = await GetConversationForUser(artifact.ConversationId, http, cfg, db, asNoTracking: true);
    if (c == null) return Results.Json(new { message = "Нет доступа к этому материалу ассистента.", code = "AI_ARTIFACT_FORBIDDEN" }, statusCode: 403);
    var dryRun = payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("dryRun", out var dry) && dry.ValueKind == JsonValueKind.True;
    var data = JsonNode.Parse(artifact.DataJson) as JsonObject ?? new JsonObject();
    var type = artifact.Type.ToLowerInvariant();
    if (dryRun) return Results.Ok(new { dryRun = true, artifact = ToArtifactDto(artifact), operations = InferApplyOperations(artifact.Type, data), patchSet = BuildPatchPreview(data) });
    if (type.Contains("course_patch_set") || string.Equals(data["type"]?.ToString(), "course_patch_set", StringComparison.OrdinalIgnoreCase) || data["patches"] is JsonArray)
    {
        return await ApplyCoursePatchSet(artifact, data, request, factory, db, ct);
    }
    if (type.Contains("assignment") || data["assignmentType"] != null || data["title"] != null)
    {
        var courseId = GuidFromNode(data["courseId"]) ?? GuidFromPayload(payload, "courseId");
        if (courseId == null) return Results.BadRequest(new { message = "Для применения задания нужен courseId.", code = "AI_ARTIFACT_COURSE_REQUIRED" });
        var body = BuildAssignmentPayload(data, payload);
        var client = factory.CreateClient();
        ForwardAuth(request, client);
        var response = await client.PostAsJsonAsync($"http://tasks-api:8080/api/courses/{courseId}/assignments", body, ct);
        var raw = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode) return Results.Content(raw, "application/json", statusCode: (int)response.StatusCode);
        artifact.Applied = true;
        artifact.AppliedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return Results.Content(raw, "application/json", statusCode: 200);
    }
    return Results.BadRequest(new { message = $"Тип материала ассистента '{artifact.Type}' нельзя применить автоматически.", code = "AI_ARTIFACT_TYPE_UNSUPPORTED", artifact.Type });
}

static object[] InferApplyOperations(string type, JsonObject data)
{
    var lower = type.ToLowerInvariant();
    if (lower.Contains("course_patch_set") || string.Equals(data["type"]?.ToString(), "course_patch_set", StringComparison.OrdinalIgnoreCase) || data["patches"] is JsonArray patches)
    {
        var patchArray = data["patches"] as JsonArray ?? new JsonArray();
        return patchArray.OfType<JsonObject>().Select(p => new
        {
            operation = p["operation"]?.ToString() ?? "update_assignment",
            assignmentId = p["assignmentId"]?.ToString(),
            title = p["title"]?.ToString(),
            changes = p["changes"] is JsonArray changes ? changes.Count : 0
        }).ToArray<object>();
    }
    if (lower.Contains("assignment") || data["assignmentType"] != null || data["title"] != null) return new object[] { new { operation = "create-assignment", title = data["title"]?.ToString() } };
    return Array.Empty<object>();
}

static object BuildPatchPreview(JsonObject data)
{
    var patches = data["patches"] as JsonArray ?? new JsonArray();
    return new
    {
        patchCount = patches.Count,
        field = data["field"]?.ToString(),
        operation = data["operation"]?.ToString(),
        patches = patches.OfType<JsonObject>().Take(200).Select(p => new
        {
            assignmentId = p["assignmentId"]?.ToString(),
            title = p["title"]?.ToString(),
            changes = p["changes"],
            diff = p["diff"]
        }).ToArray()
    };
}

static async Task<IResult> ApplyCoursePatchSet(TaskForge.Ai.Api.Domain.AiArtifact artifact, JsonObject data, HttpRequest request, IHttpClientFactory factory, AiDbContext db, CancellationToken ct)
{
    var patches = data["patches"] as JsonArray ?? new JsonArray();
    if (patches.Count == 0) return Results.BadRequest(new { message = "Patch set пустой.", code = "AI_PATCH_SET_EMPTY" });
    if (patches.Count > 250) return Results.BadRequest(new { message = "Слишком много правок за один раз. Максимум 250.", code = "AI_PATCH_SET_TOO_LARGE", count = patches.Count });

    var client = factory.CreateClient();
    ForwardAuth(request, client);
    var updated = new List<object>();
    var validations = new List<object>();

    foreach (var patch in patches.OfType<JsonObject>())
    {
        var assignmentIdText = patch["assignmentId"]?.ToString();
        if (!Guid.TryParse(assignmentIdText, out var assignmentId))
            return Results.BadRequest(new { message = $"Патч без корректного assignmentId: {patch["title"]}", code = "AI_PATCH_ASSIGNMENT_ID_REQUIRED" });

        var body = BuildAssignmentPatchPayload(patch);
        validations.Add(new { assignmentId, title = patch["title"]?.ToString(), changes = patch["changes"] is JsonArray ch ? ch.Count : 0 });
        var response = await client.PutAsJsonAsync($"http://tasks-api:8080/api/assignments/{assignmentId}", body, ct);
        var raw = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            return Results.Content(raw, response.Content.Headers.ContentType?.ToString() ?? "application/json", statusCode: (int)response.StatusCode);
        updated.Add(new { assignmentId, title = patch["title"]?.ToString(), response = ParseJson(raw) });
    }

    artifact.Applied = true;
    artifact.AppliedAtUtc = DateTimeOffset.UtcNow;
    await db.SaveChangesAsync(ct);
    return Results.Ok(new { ok = true, dryRun = false, updated, validations, message = $"Применено изменений: {updated.Count}." });
}

static object BuildAssignmentPatchPayload(JsonObject patch)
{
    var body = new JsonObject();
    if (patch["changes"] is JsonArray changes)
    {
        foreach (var change in changes.OfType<JsonObject>())
        {
            var field = (change["field"]?.ToString() ?? string.Empty).Trim();
            var value = change["newValue"]?.DeepClone();
            switch (field.ToLowerInvariant())
            {
                case "title": body["title"] = value; break;
                case "description": body["description"] = value; break;
                case "type": body["type"] = value; break;
                case "language": body["language"] = value; break;
                case "tags": body["tags"] = value; break;
                case "difficulty": body["difficulty"] = value; break;
                case "rating": body["rating"] = value; break;
                case "starterCode": body["starterCode"] = value; break;
                case "isVisible":
                case "isvisible": body["isVisible"] = value; break;
                case "codeForbiddenCalls": body["codeForbiddenCalls"] = value; break;
                case "codeRequiredCalls": body["codeRequiredCalls"] = value; break;
                default: throw new InvalidOperationException($"Поле '{field}' нельзя применить через patch set.");
            }
        }
    }
    return JsonSerializer.Deserialize<object>(body.ToJsonString(), JsonOptions()) ?? new { };
}

static object BuildAssignmentPayload(JsonObject data, JsonElement payload)
{
    var title = data["title"]?.ToString() ?? "Задание от ассистента";
    var description = data["description"]?.ToString() ?? data["statement"]?.ToString();
    var language = data["language"]?.ToString() ?? "cpp";
    var assignmentType = data["assignmentType"]?.ToString() ?? data["type"]?.ToString() ?? "code-test";
    JsonNode? tests = null;
    if (data["publicTests"] is JsonArray pub || data["hiddenTests"] is JsonArray)
    {
        tests = new JsonObject
        {
            ["publicTests"] = data["publicTests"]?.DeepClone(),
            ["hiddenTests"] = data["hiddenTests"]?.DeepClone()
        };
    }
    return new { title, description, type = assignmentType, language, starterCode = data["starterCode"]?.ToString() ?? data["referenceSolution"]?.ToString(), tests, isVisible = false };
}

static Guid? GuidFromNode(JsonNode? node) => Guid.TryParse(node?.ToString(), out var id) ? id : null;
static Guid? GuidFromPayload(JsonElement payload, string name) => payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(name, out var v) && Guid.TryParse(v.ToString(), out var id) ? id : null;
static void ForwardAuth(HttpRequest request, HttpClient client)
{
    if (request.Headers.TryGetValue("Authorization", out var auth)) client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", auth.ToString());
    if (request.Headers.TryGetValue("Cookie", out var cookie)) client.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", cookie.ToString());
}

static async Task<IResult> RunTestsBridge(AgentRunTestsRequest request, IHttpClientFactory factory, IConfiguration cfg, CancellationToken ct)
{
    var testCases = request.TestCases.HasValue && request.TestCases.Value.ValueKind == JsonValueKind.Array
        ? request.TestCases.Value.EnumerateArray().ToArray()
        : Array.Empty<JsonElement>();
    var client = factory.CreateClient();
    client.Timeout = TimeSpan.FromSeconds(90);
    using var msg = new HttpRequestMessage(HttpMethod.Post, "http://execution-api:8080/api/internal/execution/run-tests")
    {
        Content = JsonContent.Create(new { language = request.Language ?? "cpp", code = request.Code ?? string.Empty, testCases }, options: JsonOptions())
    };
    AddInternalKey(msg, cfg);
    var response = await client.SendAsync(msg, ct);
    var raw = await response.Content.ReadAsStringAsync(ct);
    return Results.Content(raw, response.Content.Headers.ContentType?.ToString() ?? "application/json", statusCode: (int)response.StatusCode);
}

static JsonSerializerOptions JsonOptions() => new(JsonSerializerDefaults.Web) { WriteIndented = false };

static void AddInternalKey(HttpRequestMessage msg, IConfiguration cfg)
{
    var key = cfg["InternalApi:Key"] ?? cfg["TaskForge:InternalKey"] ?? Environment.GetEnvironmentVariable("TASKFORGE_INTERNAL_KEY") ?? Environment.GetEnvironmentVariable("TASKFORGE_AGENT_INTERNAL_KEY");
    if (!string.IsNullOrWhiteSpace(key)) msg.Headers.TryAddWithoutValidation("X-Internal-Key", key);
}

static IEnumerable<TaskForge.Ai.Api.Domain.AiArtifact> ExtractArtifacts(Guid runId, Guid conversationId, JsonElement? result)
{
    if (!result.HasValue || result.Value.ValueKind != JsonValueKind.Object) yield break;
    JsonElement root = result.Value;
    if (root.TryGetProperty("result", out var nested) && nested.ValueKind == JsonValueKind.Object) root = nested;
    if (!root.TryGetProperty("artifacts", out var artifacts) || artifacts.ValueKind != JsonValueKind.Array) yield break;
    foreach (var item in artifacts.EnumerateArray())
    {
        if (item.ValueKind != JsonValueKind.Object) continue;
        var type = item.TryGetProperty("type", out var t) ? t.ToString() : "artifact";
        var title = item.TryGetProperty("title", out var ti) ? ti.ToString() : type;
        var data = item.TryGetProperty("data", out var d) ? d.GetRawText() : item.GetRawText();
        yield return new TaskForge.Ai.Api.Domain.AiArtifact { RunId = runId, ConversationId = conversationId, Type = string.IsNullOrWhiteSpace(type) ? "artifact" : type, Title = string.IsNullOrWhiteSpace(title) ? "Материал ассистента" : title, DataJson = string.IsNullOrWhiteSpace(data) ? "{}" : data };
    }
}

app.Run();

static object? ParseJson(string? json)
{
    if (string.IsNullOrWhiteSpace(json)) return null;
    try { return JsonSerializer.Deserialize<JsonElement>(json); }
    catch { return null; }
}

static JsonNode? ParseJsonNode(string? json)
{
    if (string.IsNullOrWhiteSpace(json)) return null;
    try { return JsonNode.Parse(json); }
    catch { return null; }
}

static string ExtractAssistantMessage(JsonElement? result)
{
    if (!result.HasValue || result.Value.ValueKind != JsonValueKind.Object) return "Готово.";
    if (result.Value.TryGetProperty("assistantMessage", out var m) && m.ValueKind == JsonValueKind.String) return m.GetString() ?? "Готово.";
    if (result.Value.TryGetProperty("message", out var directMessage) && directMessage.ValueKind == JsonValueKind.String) return directMessage.GetString() ?? "Готово.";
    if (result.Value.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.Object)
    {
        if (r.TryGetProperty("assistantMessage", out var rm) && rm.ValueKind == JsonValueKind.String) return rm.GetString() ?? "Готово.";
        if (r.TryGetProperty("message", out var nestedMessage) && nestedMessage.ValueKind == JsonValueKind.String) return nestedMessage.GetString() ?? "Готово.";
    }
    return "Готово.";
}

static string ReadMessageText(JsonElement payload)
{
    if (payload.ValueKind == JsonValueKind.String) return (payload.GetString() ?? string.Empty).Trim();
    if (payload.ValueKind != JsonValueKind.Object) return string.Empty;

    foreach (var name in new[] { "text", "content", "message", "prompt" })
    {
        if (payload.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String)
        {
            return (v.GetString() ?? string.Empty).Trim();
        }
    }

    if (payload.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object)
    {
        foreach (var name in new[] { "text", "content" })
        {
            if (message.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String)
            {
                return (v.GetString() ?? string.Empty).Trim();
            }
        }
    }

    return string.Empty;
}

static async Task<JsonObject> BuildRunPayloadAsync(
    AiConversation conversation,
    JsonElement messagePayload,
    string text,
    HttpRequest request,
    AiDbContext db,
    IHttpClientFactory factory,
    CancellationToken ct)
{
    var root = messagePayload.ValueKind == JsonValueKind.Object
        ? JsonNode.Parse(messagePayload.GetRawText()) as JsonObject ?? new JsonObject()
        : new JsonObject();

    root["type"] = "assistant_chat_turn";
    root["jobType"] = "assistant_chat_turn";
    root["scenarioId"] = "assistant_chat_turn";
    root["conversationId"] = conversation.Id.ToString();
    root["userId"] = conversation.UserId?.ToString();
    root["courseId"] = conversation.CourseId?.ToString();
    root["assignmentId"] = conversation.AssignmentId?.ToString();
    root["supportTicketId"] = conversation.SupportTicketId?.ToString();
    root["rawText"] = text;
    root["message"] = new JsonObject { ["text"] = text };
    root["request"] = new JsonObject
    {
        ["path"] = request.Path.ToString(),
        ["method"] = request.Method
    };

    var recentMessages = await db.Messages.AsNoTracking()
        .Where(x => x.ConversationId == conversation.Id)
        .OrderByDescending(x => x.CreatedAtUtc)
        .Take(80)
        .OrderBy(x => x.CreatedAtUtc)
        .Select(x => new { x.Id, x.RunId, x.Role, x.Content, x.ClientMessageId, x.CreatedAtUtc })
        .ToListAsync(ct);

    var recentMessagesArray = new JsonArray();
    foreach (var item in recentMessages)
    {
        recentMessagesArray.Add(new JsonObject
        {
            ["id"] = item.Id.ToString(),
            ["runId"] = item.RunId?.ToString(),
            ["role"] = item.Role,
            ["text"] = item.Content,
            ["clientMessageId"] = item.ClientMessageId,
            ["createdAtUtc"] = item.CreatedAtUtc.ToString("O")
        });
    }
    root["recentMessages"] = recentMessagesArray;

    var runHistory = await db.Runs.AsNoTracking()
        .Where(x => x.ConversationId == conversation.Id)
        .OrderByDescending(x => x.CreatedAtUtc)
        .Take(20)
        .OrderBy(x => x.CreatedAtUtc)
        .ToListAsync(ct);

    var runIds = runHistory.Select(x => x.Id).ToHashSet();
    var stepHistory = await db.Steps.AsNoTracking()
        .Where(x => x.ConversationId == conversation.Id && runIds.Contains(x.RunId))
        .OrderBy(x => x.CreatedAtUtc)
        .ThenBy(x => x.Seq)
        .Take(500)
        .ToListAsync(ct);

    var artifactHistory = await db.Artifacts.AsNoTracking()
        .Where(x => x.ConversationId == conversation.Id && runIds.Contains(x.RunId))
        .OrderBy(x => x.CreatedAtUtc)
        .Take(80)
        .ToListAsync(ct);

    var runsArray = new JsonArray();
    foreach (var runItem in runHistory)
    {
        runsArray.Add(new JsonObject
        {
            ["id"] = runItem.Id.ToString(),
            ["status"] = runItem.Status,
            ["jobType"] = runItem.JobType,
            ["workerId"] = runItem.WorkerId,
            ["createdAtUtc"] = runItem.CreatedAtUtc.ToString("O"),
            ["startedAtUtc"] = runItem.StartedAtUtc?.ToString("O"),
            ["completedAtUtc"] = runItem.CompletedAtUtc?.ToString("O"),
            ["error"] = string.IsNullOrWhiteSpace(runItem.ErrorJson) ? null : ParseJsonNode(runItem.ErrorJson)
        });
    }

    var stepsArray = new JsonArray();
    foreach (var stepItem in stepHistory)
    {
        stepsArray.Add(new JsonObject
        {
            ["id"] = stepItem.Id.ToString(),
            ["runId"] = stepItem.RunId.ToString(),
            ["seq"] = stepItem.Seq,
            ["kind"] = stepItem.Kind,
            ["status"] = stepItem.Status,
            ["actionName"] = stepItem.ActionName,
            ["title"] = stepItem.Title,
            ["summary"] = stepItem.Summary,
            ["data"] = string.IsNullOrWhiteSpace(stepItem.DataJson) ? null : ParseJsonNode(stepItem.DataJson),
            ["isVisibleToUser"] = stepItem.IsVisibleToUser,
            ["createdAtUtc"] = stepItem.CreatedAtUtc.ToString("O")
        });
    }

    var artifactsArray = new JsonArray();
    foreach (var artifactItem in artifactHistory)
    {
        artifactsArray.Add(new JsonObject
        {
            ["id"] = artifactItem.Id.ToString(),
            ["runId"] = artifactItem.RunId.ToString(),
            ["type"] = artifactItem.Type,
            ["title"] = artifactItem.Title,
            ["applied"] = artifactItem.Applied,
            ["createdAtUtc"] = artifactItem.CreatedAtUtc.ToString("O")
        });
    }

    root["conversationState"] = new JsonObject
    {
        ["messages"] = recentMessagesArray.DeepClone(),
        ["runs"] = runsArray,
        ["steps"] = stepsArray,
        ["artifacts"] = artifactsArray,
        ["counts"] = new JsonObject
        {
            ["messages"] = recentMessagesArray.Count,
            ["runs"] = runsArray.Count,
            ["steps"] = stepsArray.Count,
            ["artifacts"] = artifactsArray.Count
        }
    };

    var warnings = new JsonArray();
    var client = factory.CreateClient();
    client.Timeout = TimeSpan.FromSeconds(20);
    ForwardAuth(request, client);

    JsonNode? course = null;
    JsonNode? assignments = null;

    if (conversation.CourseId.HasValue)
    {
        course = await FetchJsonOrWarningAsync(client, $"http://education-api:8080/api/courses/{conversation.CourseId}", "course", warnings, ct);
        assignments = await FetchJsonOrWarningAsync(client, $"http://tasks-api:8080/api/courses/{conversation.CourseId}/assignments", "assignments", warnings, ct);
    }

    if (course != null) root["course"] = course;
    if (assignments != null)
    {
        root["assignments"] = assignments.DeepClone();
        root["courseOutline"] = BuildCourseOutline(assignments);
        if (conversation.AssignmentId.HasValue)
        {
            root["targetAssignments"] = SelectAssignments(assignments, conversation.AssignmentId.Value);
            root["focusAssignments"] = SelectAssignments(assignments, conversation.AssignmentId.Value);
        }
    }

    root["courseDigest"] = new JsonObject
    {
        ["selectedCourseId"] = conversation.CourseId?.ToString(),
        ["selectedAssignmentId"] = conversation.AssignmentId?.ToString(),
        ["assignmentCount"] = CountAssignments(assignments)
    };

    if (messagePayload.ValueKind == JsonValueKind.Object && messagePayload.TryGetProperty("attachments", out var attachments))
    {
        root["attachments"] = JsonNode.Parse(attachments.GetRawText());
    }

    if (warnings.Count > 0) root["contextWarnings"] = warnings;
    return root;
}

static async Task<JsonNode?> FetchJsonOrWarningAsync(HttpClient client, string url, string label, JsonArray warnings, CancellationToken ct)
{
    try
    {
        using var response = await client.GetAsync(url, ct);
        var raw = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            warnings.Add(new JsonObject
            {
                ["source"] = label,
                ["status"] = (int)response.StatusCode,
                ["message"] = "Контекст не получен от связанного сервиса."
            });
            return null;
        }
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return JsonNode.Parse(raw);
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
    {
        warnings.Add(new JsonObject
        {
            ["source"] = label,
            ["message"] = ex.Message
        });
        return null;
    }
}

static JsonNode? BuildCourseOutline(JsonNode? assignments)
{
    var array = ExtractArray(assignments);
    if (array == null) return null;

    var outline = new JsonArray();
    foreach (var item in array.Take(80))
    {
        if (item is not JsonObject obj) continue;
        outline.Add(new JsonObject
        {
            ["id"] = obj["id"]?.ToString() ?? obj["Id"]?.ToString(),
            ["title"] = obj["title"]?.ToString() ?? obj["Title"]?.ToString(),
            ["type"] = obj["type"]?.ToString() ?? obj["assignmentType"]?.ToString() ?? obj["AssignmentType"]?.ToString(),
            ["order"] = obj["order"]?.DeepClone() ?? obj["Order"]?.DeepClone()
        });
    }
    return outline;
}

static JsonArray SelectAssignments(JsonNode? assignments, Guid assignmentId)
{
    var result = new JsonArray();
    var array = ExtractArray(assignments);
    if (array == null) return result;

    foreach (var item in array)
    {
        if (item is not JsonObject obj) continue;
        var rawId = obj["id"]?.ToString() ?? obj["Id"]?.ToString();
        if (Guid.TryParse(rawId, out var id) && id == assignmentId)
        {
            result.Add(obj.DeepClone());
        }
    }
    return result;
}

static int CountAssignments(JsonNode? assignments)
{
    var array = ExtractArray(assignments);
    return array?.Count ?? 0;
}

static JsonArray? ExtractArray(JsonNode? node)
{
    if (node is JsonArray direct) return direct;
    if (node is not JsonObject obj) return null;
    foreach (var key in new[] { "assignments", "items", "data", "rows", "tasks" })
    {
        if (obj[key] is JsonArray array) return array;
    }
    return null;
}

static AiStep BuildStep(AiRun run, AgentStepRequest request, int seq)
{
    var step = request.Step;
    var title = ReadStepString(step, "title", ReadStepString(step, "message", "AI обрабатывает запрос"));
    var kind = ReadStepString(step, "kind", "worker");
    var status = ReadStepString(step, "status", "running");
    var actionName = ReadStepString(step, "actionName", ReadStepString(step, "action", "agent_step"));
    var summary = ReadStepString(step, "summary", string.Empty);
    var visible = ReadStepBool(step, "isVisibleToUser", true);

    return new AiStep
    {
        RunId = run.Id,
        ConversationId = run.ConversationId,
        Seq = seq,
        Kind = string.IsNullOrWhiteSpace(kind) ? "worker" : kind,
        Status = string.IsNullOrWhiteSpace(status) ? "running" : status,
        ActionName = string.IsNullOrWhiteSpace(actionName) ? "agent_step" : actionName,
        Title = string.IsNullOrWhiteSpace(title) ? "AI обрабатывает запрос" : title,
        Summary = string.IsNullOrWhiteSpace(summary) ? null : summary,
        DataJson = step.HasValue ? step.Value.GetRawText() : null,
        IsVisibleToUser = visible
    };
}

static string ReadStepString(JsonElement? step, string name, string fallback)
{
    if (!step.HasValue || step.Value.ValueKind != JsonValueKind.Object) return fallback;
    return step.Value.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
        ? v.GetString() ?? fallback
        : fallback;
}

static bool ReadStepBool(JsonElement? step, string name, bool fallback)
{
    if (!step.HasValue || step.Value.ValueKind != JsonValueKind.Object) return fallback;
    return step.Value.TryGetProperty(name, out var v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False)
        ? v.GetBoolean()
        : fallback;
}

static async Task BroadcastAgentEventAsync(IHubContext<AgentRealtimeHub> hub, Guid conversationId, string type, object payload, CancellationToken ct)
{
    await hub.Clients.Group(AgentRealtimeHub.ConversationGroup(conversationId)).SendAsync("AgentEvent", new
    {
        type,
        conversationId,
        payload
    }, ct);
}

static object ToConversationDto(AiConversation x) => new
{
    x.Id,
    x.Title,
    x.UserId,
    x.CourseId,
    x.AssignmentId,
    x.SupportTicketId,
    x.CreatedAtUtc,
    x.UpdatedAtUtc
};

static object ToMessageDto(AiMessage x, IReadOnlyList<AiArtifact>? artifacts = null) => new
{
    x.Id,
    x.ConversationId,
    x.RunId,
    x.Role,
    text = x.Content,
    content = x.Content,
    x.Content,
    data = artifacts is { Count: > 0 } ? new { artifacts = artifacts.Select(ToArtifactDto).ToList() } : null,
    x.ClientMessageId,
    x.CreatedAtUtc
};

static object ToRunDto(AiRun x, IReadOnlyList<AiArtifact>? artifacts = null, IReadOnlyList<AiStep>? steps = null) => new
{
    x.Id,
    x.ConversationId,
    x.Status,
    x.JobType,
    x.WorkerId,
    x.StartedAtUtc,
    x.CompletedAtUtc,
    x.CreatedAtUtc,
    x.UpdatedAtUtc,
    artifacts = (artifacts ?? Array.Empty<AiArtifact>()).Select(ToArtifactDto).ToList(),
    steps = (steps ?? Array.Empty<AiStep>()).Where(s => s.IsVisibleToUser).Select(ToStepDto).ToList()
};

static object ToStepDto(AiStep x) => new
{
    x.Id,
    x.RunId,
    x.ConversationId,
    x.Seq,
    x.Kind,
    x.Status,
    x.ActionName,
    x.Title,
    x.Summary,
    data = ParseJson(x.DataJson),
    x.IsVisibleToUser,
    x.CreatedAtUtc
};

static object ToArtifactDto(AiArtifact x) => new
{
    x.Id,
    artifactId = x.Id,
    x.RunId,
    x.ConversationId,
    x.Type,
    x.Title,
    data = ParseJson(x.DataJson),
    x.Applied,
    x.AppliedAtUtc,
    x.CreatedAtUtc
};

public sealed class AgentRealtimeHub : Hub
{
    public static string ConversationGroup(Guid conversationId) => $"agent-conversation-{conversationId:N}";

    public static string ConversationGroup(string conversationId)
        => Guid.TryParse(conversationId, out var parsed)
            ? ConversationGroup(parsed)
            : $"agent-conversation-{conversationId}";

    public Task JoinConversation(string conversationId)
        => Groups.AddToGroupAsync(Context.ConnectionId, ConversationGroup(conversationId));

    public Task LeaveConversation(string conversationId)
        => Groups.RemoveFromGroupAsync(Context.ConnectionId, ConversationGroup(conversationId));
}

public sealed record AgentClaimNextRequest(string WorkerId);
public sealed record AgentWorkerRequest(string WorkerId);
public sealed record AgentStepRequest(string WorkerId, JsonElement? Step);
public sealed record AgentCompleteRequest(string WorkerId, JsonElement? Result);
public sealed record AgentFailRequest(string WorkerId, JsonElement? Error);
public sealed record AgentRunTestsRequest(Guid? RunId, string? WorkerId, string? Language, string? Code, JsonElement? TestCases);
