using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using TaskForge.Ai.Api.Data;

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

// Compatibility endpoints for the extracted .NET AI worker.
// They intentionally return an empty queue until real AI run dispatch is wired to task/content services.
// This prevents noisy 404 polling loops while keeping the worker and ai-api contract stable.

app.MapGet("/api/agent/conversations", async (AiDbContext db) =>
{
    var rows = await db.Conversations.AsNoTracking().OrderByDescending(x => x.UpdatedAtUtc).Take(100).ToListAsync();
    return Results.Ok(rows.Select(ToConversationDto).ToList());
});

app.MapPost("/api/agent/conversations", async (JsonElement payload, AiDbContext db) =>
{
    var title = payload.TryGetProperty("title", out var t) ? t.GetString() : null;
    var c = new TaskForge.Ai.Api.Domain.AiConversation { Title = string.IsNullOrWhiteSpace(title) ? "Новый диалог" : title!.Trim() };
    db.Conversations.Add(c);
    await db.SaveChangesAsync();
    return Results.Ok(ToConversationDto(c));
});

app.MapGet("/api/agent/conversations/{conversationId:guid}", async (Guid conversationId, AiDbContext db) =>
{
    var c = await db.Conversations.AsNoTracking().FirstOrDefaultAsync(x => x.Id == conversationId);
    if (c == null) return Results.NotFound();
    var messageRows = await db.Messages.AsNoTracking().Where(x => x.ConversationId == conversationId).OrderBy(x => x.CreatedAtUtc).ToListAsync();
    var runRows = await db.Runs.AsNoTracking().Where(x => x.ConversationId == conversationId).OrderByDescending(x => x.CreatedAtUtc).ToListAsync();
    var artifactRows = await db.Artifacts.AsNoTracking().Where(x => x.ConversationId == conversationId).OrderBy(x => x.CreatedAtUtc).ToListAsync();
    var messages = messageRows.Select(ToMessageDto).ToList();
    var runs = runRows.Select(r => ToRunDto(r, artifactRows.Where(a => a.RunId == r.Id).ToList())).ToList();
    return Results.Ok(new { conversation = ToConversationDto(c), messages, runs, artifacts = artifactRows.Select(ToArtifactDto).ToList() });
});

app.MapPost("/api/agent/conversations/{conversationId:guid}/messages", async (Guid conversationId, JsonElement payload, AiDbContext db) =>
{
    var c = await db.Conversations.FindAsync(conversationId);
    if (c == null) return Results.NotFound(new { message = "Диалог не найден.", code = "AI_CONVERSATION_NOT_FOUND" });
    var text = payload.TryGetProperty("content", out var content) ? content.GetString() : payload.TryGetProperty("message", out var message) ? message.GetString() : string.Empty;
    var clientId = payload.TryGetProperty("clientMessageId", out var cmid) ? cmid.GetString() : null;
    var userMessage = new TaskForge.Ai.Api.Domain.AiMessage { ConversationId = conversationId, Role = "user", Content = text ?? string.Empty, ClientMessageId = clientId };
    var run = new TaskForge.Ai.Api.Domain.AiRun
    {
        ConversationId = conversationId,
        Status = "queued",
        JobType = "assistant_chat_turn",
        PayloadJson = JsonSerializer.Serialize(new { conversationId, rawText = text ?? string.Empty, message = new { text = text ?? string.Empty }, clientMessageId = clientId })
    };
    c.UpdatedAtUtc = DateTimeOffset.UtcNow;
    db.Messages.Add(userMessage);
    db.Runs.Add(run);
    await db.SaveChangesAsync();
    return Results.Ok(new { conversation = ToConversationDto(c), message = ToMessageDto(userMessage), run = ToRunDto(run), queued = true });
});

app.MapPost("/api/agent/conversations/{conversationId:guid}/attachments", async (Guid conversationId, HttpRequest request, AiDbContext db, CancellationToken ct) => await SaveAttachment(conversationId, request, db, ct)).DisableAntiforgery();
app.MapPost("/api/agent/conversations/{conversationId:guid}/polish-task", async (Guid conversationId, JsonElement payload, AiDbContext db) => await QueueAgentRun(conversationId, "polish_assignment_draft", payload, db));
app.MapPost("/api/agent/conversations/{conversationId:guid}/polish-tasks", async (Guid conversationId, JsonElement payload, AiDbContext db) => await QueueAgentRun(conversationId, "polish_assignment_draft", payload, db));
app.MapPost("/api/agent/runs/{runId:guid}/cancel", async (Guid runId, AiDbContext db) =>
{
    var run = await db.Runs.FindAsync(runId);
    if (run != null) { run.Status = "canceled"; run.UpdatedAtUtc = DateTimeOffset.UtcNow; await db.SaveChangesAsync(); }
    return Results.Ok(new { runId, status = "canceled" });
});
app.MapPost("/api/agent/artifacts/{artifactId:guid}/apply", async (Guid artifactId, JsonElement payload, HttpRequest request, AiDbContext db, IHttpClientFactory factory, CancellationToken ct) => await ApplyArtifact(null, artifactId, payload, request, db, factory, ct));
app.MapPost("/api/agent/runs/{runId:guid}/artifacts/{artifactId:guid}/apply", async (Guid runId, Guid artifactId, JsonElement payload, HttpRequest request, AiDbContext db, IHttpClientFactory factory, CancellationToken ct) => await ApplyArtifact(runId, artifactId, payload, request, db, factory, ct));
app.MapGet("/api/agent/conversations/{conversationId:guid}/debug-dump", async (Guid conversationId, AiDbContext db) =>
{
    var messages = await db.Messages.AsNoTracking().Where(x => x.ConversationId == conversationId).OrderBy(x => x.CreatedAtUtc).ToListAsync();
    return Results.Text(string.Join("\n", messages.Select(x => $"[{x.CreatedAtUtc:O}] {x.Role}: {x.Content}")), "text/plain");
});
app.MapHub<AgentRealtimeHub>("/hubs/agent");

app.MapPost("/api/internal/agent/claim-next", async (AgentClaimNextRequest request, AiDbContext db) =>
{
    var run = await db.Runs.Where(x => x.Status == "queued").OrderBy(x => x.CreatedAtUtc).FirstOrDefaultAsync();
    if (run == null) return Results.Ok(new { ok = true, request.WorkerId, job = (object?)null });
    run.Status = "running";
    run.WorkerId = request.WorkerId;
    run.StartedAtUtc = DateTimeOffset.UtcNow;
    run.UpdatedAtUtc = DateTimeOffset.UtcNow;
    await db.SaveChangesAsync();
    var payload = ParseJson(run.PayloadJson) ?? new { conversationId = run.ConversationId, rawText = "" };
    return Results.Ok(new { ok = true, request.WorkerId, job = new { id = run.Id, runId = run.Id, run.ConversationId, jobType = run.JobType, type = run.JobType, payload } });
});

app.MapPost("/api/internal/agent/runs/{runId:guid}/heartbeat", async (Guid runId, AgentWorkerRequest request, AiDbContext db) =>
{
    var run = await db.Runs.FindAsync(runId);
    if (run != null)
    {
        run.WorkerId = request.WorkerId;
        run.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
    }
    return Results.Ok(new { ok = true, runId, request.WorkerId });
});

app.MapPost("/api/internal/agent/runs/{runId:guid}/steps", async (Guid runId, AgentStepRequest request, AiDbContext db) =>
{
    var run = await db.Runs.FindAsync(runId);
    if (run == null) return Results.NotFound(new { message = "AI run не найден.", code = "AI_RUN_NOT_FOUND" });
    run.UpdatedAtUtc = DateTimeOffset.UtcNow;
    await db.SaveChangesAsync();
    return Results.Ok(new { ok = true, runId, request.WorkerId });
});

app.MapPost("/api/internal/agent/runs/{runId:guid}/complete", async (Guid runId, AgentCompleteRequest request, AiDbContext db) =>
{
    var run = await db.Runs.FindAsync(runId);
    if (run == null) return Results.NotFound(new { message = "AI run не найден.", code = "AI_RUN_NOT_FOUND" });
    run.Status = "completed";
    run.WorkerId = request.WorkerId;
    run.CompletedAtUtc = DateTimeOffset.UtcNow;
    run.UpdatedAtUtc = DateTimeOffset.UtcNow;
    var text = ExtractAssistantMessage(request.Result);
    db.Messages.Add(new TaskForge.Ai.Api.Domain.AiMessage { ConversationId = run.ConversationId, Role = "assistant", Content = text });
    foreach (var artifact in ExtractArtifacts(run.Id, run.ConversationId, request.Result)) db.Artifacts.Add(artifact);
    var c = await db.Conversations.FindAsync(run.ConversationId);
    if (c != null) c.UpdatedAtUtc = DateTimeOffset.UtcNow;
    await db.SaveChangesAsync();
    return Results.Ok(new { ok = true, runId, request.WorkerId, status = "completed" });
});

app.MapPost("/api/internal/agent/runs/{runId:guid}/fail", async (Guid runId, AgentFailRequest request, AiDbContext db) =>
{
    var run = await db.Runs.FindAsync(runId);
    if (run != null)
    {
        run.Status = "failed";
        run.WorkerId = request.WorkerId;
        run.ErrorJson = request.Error?.GetRawText();
        run.CompletedAtUtc = DateTimeOffset.UtcNow;
        run.UpdatedAtUtc = DateTimeOffset.UtcNow;
        db.Messages.Add(new TaskForge.Ai.Api.Domain.AiMessage { ConversationId = run.ConversationId, Role = "assistant", Content = "AI-ассистент не смог завершить задачу. Проверьте настройки LLM-провайдера и логи ai-worker." });
        await db.SaveChangesAsync();
    }
    return Results.Ok(new { ok = true, runId, request.WorkerId, status = "failed" });
});

app.MapPost("/api/internal/agent/tools/run-tests", async (AgentRunTestsRequest request, IHttpClientFactory factory, IConfiguration cfg, CancellationToken ct) => await RunTestsBridge(request, factory, cfg, ct));


static async Task<IResult> SaveAttachment(Guid conversationId, HttpRequest request, AiDbContext db, CancellationToken ct)
{
    var c = await db.Conversations.FindAsync(new object[] { conversationId }, ct);
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

static async Task<IResult> QueueAgentRun(Guid conversationId, string jobType, JsonElement payload, AiDbContext db)
{
    var c = await db.Conversations.FindAsync(conversationId);
    if (c == null) return Results.NotFound(new { message = "Диалог не найден.", code = "AI_CONVERSATION_NOT_FOUND" });
    var root = JsonNode.Parse(payload.GetRawText()) as JsonObject ?? new JsonObject();
    root["conversationId"] = conversationId.ToString();
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
    await db.SaveChangesAsync();
    return Results.Ok(new { run = ToRunDto(run), queued = true });
}

static async Task<IResult> ApplyArtifact(Guid? runId, Guid artifactId, JsonElement payload, HttpRequest request, AiDbContext db, IHttpClientFactory factory, CancellationToken ct)
{
    var artifact = await db.Artifacts.FirstOrDefaultAsync(x => x.Id == artifactId && (!runId.HasValue || x.RunId == runId.Value), ct);
    if (artifact == null) return Results.NotFound(new { message = "AI-артефакт не найден. Обновите диалог и попробуйте снова.", code = "AI_ARTIFACT_NOT_FOUND" });
    var dryRun = payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("dryRun", out var dry) && dry.ValueKind == JsonValueKind.True;
    var data = JsonNode.Parse(artifact.DataJson) as JsonObject ?? new JsonObject();
    if (dryRun) return Results.Ok(new { dryRun = true, artifact = ToArtifactDto(artifact), operations = InferApplyOperations(artifact.Type, data) });
    var type = artifact.Type.ToLowerInvariant();
    if (type.Contains("assignment") || data["assignmentType"] != null || data["title"] != null)
    {
        var courseId = GuidFromNode(data["courseId"]) ?? GuidFromPayload(payload, "courseId");
        if (courseId == null) return Results.BadRequest(new { message = "Для применения AI-задания нужен courseId.", code = "AI_ARTIFACT_COURSE_REQUIRED" });
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
    return Results.BadRequest(new { message = $"Тип AI-артефакта '{artifact.Type}' нельзя применить автоматически.", code = "AI_ARTIFACT_TYPE_UNSUPPORTED", artifact.Type });
}

static object[] InferApplyOperations(string type, JsonObject data)
{
    var lower = type.ToLowerInvariant();
    if (lower.Contains("assignment") || data["assignmentType"] != null || data["title"] != null) return new object[] { new { operation = "create-assignment", title = data["title"]?.ToString() } };
    return Array.Empty<object>();
}

static object BuildAssignmentPayload(JsonObject data, JsonElement payload)
{
    var title = data["title"]?.ToString() ?? "AI задание";
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
        yield return new TaskForge.Ai.Api.Domain.AiArtifact { RunId = runId, ConversationId = conversationId, Type = string.IsNullOrWhiteSpace(type) ? "artifact" : type, Title = string.IsNullOrWhiteSpace(title) ? "AI artifact" : title, DataJson = string.IsNullOrWhiteSpace(data) ? "{}" : data };
    }
}

app.Run();

static object? ParseJson(string? json) { if (string.IsNullOrWhiteSpace(json)) return null; try { return JsonSerializer.Deserialize<JsonElement>(json); } catch { return null; } }
static string ExtractAssistantMessage(JsonElement? result)
{
    if (!result.HasValue || result.Value.ValueKind != JsonValueKind.Object) return "Готово.";
    if (result.Value.TryGetProperty("assistantMessage", out var m) && m.ValueKind == JsonValueKind.String) return m.GetString() ?? "Готово.";
    if (result.Value.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.Object && r.TryGetProperty("assistantMessage", out var rm)) return rm.GetString() ?? "Готово.";
    return "Готово.";
}
static object ToConversationDto(TaskForge.Ai.Api.Domain.AiConversation x) => new { x.Id, x.Title, x.UserId, x.CreatedAtUtc, x.UpdatedAtUtc };
static object ToMessageDto(TaskForge.Ai.Api.Domain.AiMessage x) => new { x.Id, x.ConversationId, x.Role, x.Content, x.ClientMessageId, x.CreatedAtUtc };
static object ToRunDto(TaskForge.Ai.Api.Domain.AiRun x, IReadOnlyList<TaskForge.Ai.Api.Domain.AiArtifact>? artifacts = null) => new { x.Id, x.ConversationId, x.Status, x.JobType, x.WorkerId, x.StartedAtUtc, x.CompletedAtUtc, x.CreatedAtUtc, x.UpdatedAtUtc, artifacts = (artifacts ?? Array.Empty<TaskForge.Ai.Api.Domain.AiArtifact>()).Select(ToArtifactDto).ToList() };
static object ToArtifactDto(TaskForge.Ai.Api.Domain.AiArtifact x) => new { x.Id, artifactId = x.Id, x.RunId, x.ConversationId, x.Type, x.Title, data = ParseJson(x.DataJson), x.Applied, x.AppliedAtUtc, x.CreatedAtUtc };
public sealed class AgentRealtimeHub : Hub { }

public sealed record AgentClaimNextRequest(string WorkerId);
public sealed record AgentWorkerRequest(string WorkerId);
public sealed record AgentStepRequest(string WorkerId, JsonElement? Step);
public sealed record AgentCompleteRequest(string WorkerId, JsonElement? Result);
public sealed record AgentFailRequest(string WorkerId, JsonElement? Error);
public sealed record AgentRunTestsRequest(Guid? RunId, string? WorkerId, string? Language, string? Code, JsonElement? TestCases);
