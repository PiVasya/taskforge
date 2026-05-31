using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using TaskForge.Ai.Api.Data;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHealthChecks();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddSignalR();
builder.Services.AddDbContext<AiDbContext>(options =>
{
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection"));
});

var app = builder.Build();

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
    migrations = "not generated; see MIGRATIONS_REQUIRED.md",
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
    var messages = messageRows.Select(ToMessageDto).ToList();
    var runs = runRows.Select(ToRunDto).ToList();
    return Results.Ok(new { conversation = ToConversationDto(c), messages, runs, artifacts = Array.Empty<object>() });
});

app.MapPost("/api/agent/conversations/{conversationId:guid}/messages", async (Guid conversationId, JsonElement payload, AiDbContext db) =>
{
    var c = await db.Conversations.FindAsync(conversationId);
    if (c == null) return Results.NotFound();
    var text = payload.TryGetProperty("content", out var content) ? content.GetString() : payload.TryGetProperty("message", out var message) ? message.GetString() : string.Empty;
    var clientId = payload.TryGetProperty("clientMessageId", out var cmid) ? cmid.GetString() : null;
    var userMessage = new TaskForge.Ai.Api.Domain.AiMessage { ConversationId = conversationId, Role = "user", Content = text ?? string.Empty, ClientMessageId = clientId };
    var assistantMessage = new TaskForge.Ai.Api.Domain.AiMessage { ConversationId = conversationId, Role = "assistant", Content = "AI microservice запущен. Реальная генерация будет подключена к ai-worker/provider на следующем шаге." };
    var run = new TaskForge.Ai.Api.Domain.AiRun { ConversationId = conversationId, Status = "completed" };
    c.UpdatedAtUtc = DateTimeOffset.UtcNow;
    db.Messages.AddRange(userMessage, assistantMessage);
    db.Runs.Add(run);
    await db.SaveChangesAsync();
    return Results.Ok(new { message = ToMessageDto(assistantMessage), userMessage = ToMessageDto(userMessage), run = ToRunDto(run), artifacts = Array.Empty<object>() });
});

app.MapPost("/api/agent/conversations/{conversationId:guid}/attachments", (Guid conversationId) => Results.Ok(new { id = Guid.NewGuid(), conversationId, status = "uploaded" }));
app.MapPost("/api/agent/conversations/{conversationId:guid}/polish-task", (Guid conversationId, JsonElement payload) => Results.Ok(new { conversationId, result = payload }));
app.MapPost("/api/agent/conversations/{conversationId:guid}/polish-tasks", (Guid conversationId, JsonElement payload) => Results.Ok(new { conversationId, result = payload }));
app.MapPost("/api/agent/runs/{runId:guid}/cancel", async (Guid runId, AiDbContext db) =>
{
    var run = await db.Runs.FindAsync(runId);
    if (run != null) { run.Status = "canceled"; run.UpdatedAtUtc = DateTimeOffset.UtcNow; await db.SaveChangesAsync(); }
    return Results.Ok(new { runId, status = "canceled" });
});
app.MapPost("/api/agent/artifacts/{artifactId:guid}/apply", (Guid artifactId, JsonElement payload) => Results.Ok(new { artifactId, applied = false, message = "Artifact apply bridge is not wired yet." }));
app.MapPost("/api/agent/runs/{runId:guid}/artifacts/{artifactId:guid}/apply", (Guid runId, Guid artifactId, JsonElement payload) => Results.Ok(new { runId, artifactId, applied = false, message = "Artifact apply bridge is not wired yet." }));
app.MapGet("/api/agent/conversations/{conversationId:guid}/debug-dump", async (Guid conversationId, AiDbContext db) =>
{
    var messages = await db.Messages.AsNoTracking().Where(x => x.ConversationId == conversationId).OrderBy(x => x.CreatedAtUtc).ToListAsync();
    return Results.Text(string.Join("\n", messages.Select(x => $"[{x.CreatedAtUtc:O}] {x.Role}: {x.Content}")), "text/plain");
});
app.MapHub<AgentRealtimeHub>("/hubs/agent");

app.MapPost("/api/internal/agent/claim-next", (AgentClaimNextRequest request) => Results.Ok(new
{
    ok = true,
    workerId = request.WorkerId,
    job = (object?)null
}));

app.MapPost("/api/internal/agent/runs/{runId:guid}/heartbeat", (Guid runId, AgentWorkerRequest request) => Results.Ok(new
{
    ok = true,
    runId,
    request.WorkerId
}));

app.MapPost("/api/internal/agent/runs/{runId:guid}/steps", (Guid runId, AgentStepRequest request) => Results.Ok(new
{
    ok = true,
    runId,
    request.WorkerId
}));

app.MapPost("/api/internal/agent/runs/{runId:guid}/complete", (Guid runId, AgentCompleteRequest request) => Results.Ok(new
{
    ok = true,
    runId,
    request.WorkerId,
    status = "completed"
}));

app.MapPost("/api/internal/agent/runs/{runId:guid}/fail", (Guid runId, AgentFailRequest request) => Results.Ok(new
{
    ok = true,
    runId,
    request.WorkerId,
    status = "failed"
}));

app.MapPost("/api/internal/agent/tools/run-tests", (AgentRunTestsRequest request) => Results.Ok(new
{
    ok = false,
    status = "not_configured",
    message = "AI run-tests bridge is not wired yet. Use execution-service integration in the next implementation step.",
    request.RunId,
    request.WorkerId
}));

app.Run();

static object ToConversationDto(TaskForge.Ai.Api.Domain.AiConversation x) => new { x.Id, x.Title, x.UserId, x.CreatedAtUtc, x.UpdatedAtUtc };
static object ToMessageDto(TaskForge.Ai.Api.Domain.AiMessage x) => new { x.Id, x.ConversationId, x.Role, x.Content, x.ClientMessageId, x.CreatedAtUtc };
static object ToRunDto(TaskForge.Ai.Api.Domain.AiRun x) => new { x.Id, x.ConversationId, x.Status, x.CreatedAtUtc, x.UpdatedAtUtc };
public sealed class AgentRealtimeHub : Hub { }

public sealed record AgentClaimNextRequest(string WorkerId);
public sealed record AgentWorkerRequest(string WorkerId);
public sealed record AgentStepRequest(string WorkerId, JsonElement? Step);
public sealed record AgentCompleteRequest(string WorkerId, JsonElement? Result);
public sealed record AgentFailRequest(string WorkerId, JsonElement? Error);
public sealed record AgentRunTestsRequest(Guid? RunId, string? WorkerId, string? Language, string? Code, JsonElement? TestCases);
