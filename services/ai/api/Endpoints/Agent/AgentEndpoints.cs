using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using TaskForge.Ai.Api.Data;
using TaskForge.Ai.Api.Domain;

using TaskForge.Ai.Api.Contracts;
using TaskForge.Ai.Api.Hubs;
using static TaskForge.Ai.Api.Services.Access.AiApiAccessService;
using static TaskForge.Ai.Api.Services.Common.AiApiCommonService;
using static TaskForge.Ai.Api.Services.Mapping.AiApiMappingService;
using static TaskForge.Ai.Api.Services.Results.AiApiResultsService;
using static TaskForge.Ai.Api.Services.Serialization.AiApiSerializationService;
using static TaskForge.Ai.Api.Services.Testing.AiApiTestingService;

namespace TaskForge.Ai.Api.Endpoints;

internal static partial class AiApiEndpoints
{
    private static WebApplication MapAgentEndpoints(WebApplication app)
    {
        app.MapGet("/api/agent/conversations", async (HttpContext http, IConfiguration cfg, AiDbContext db, Guid? courseId, Guid? assignmentId) =>
        {
            var uid = CurrentUserId(http, cfg);
            if (uid == null) return Microsoft.AspNetCore.Http.Results.Unauthorized();
            var q = db.Conversations.AsNoTracking();
            if (!IsEditorOrAdmin(http)) q = q.Where(x => x.UserId == uid.Value);
            if (courseId.HasValue) q = q.Where(x => x.CourseId == courseId.Value);
            if (assignmentId.HasValue) q = q.Where(x => x.AssignmentId == assignmentId.Value);
            var rows = await q.OrderByDescending(x => x.UpdatedAtUtc).Take(100).ToListAsync();
            return Microsoft.AspNetCore.Http.Results.Ok(rows.Select(ToConversationDto).ToList());
        });

        app.MapPost("/api/agent/conversations", async (JsonElement payload, HttpContext http, IConfiguration cfg, AiDbContext db, CancellationToken ct) =>
        {
            var uid = CurrentUserId(http, cfg);
            if (uid == null) return Microsoft.AspNetCore.Http.Results.Unauthorized();
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
            return Microsoft.AspNetCore.Http.Results.Ok(new { conversation = ToConversationDto(c) });
        });

        app.MapGet("/api/agent/conversations/{conversationId:guid}", async (Guid conversationId, HttpContext http, IConfiguration cfg, AiDbContext db, CancellationToken ct) =>
        {
            var c = await GetConversationForUser(conversationId, http, cfg, db, asNoTracking: true);
            if (c == null) return Microsoft.AspNetCore.Http.Results.NotFound();
            var messageRows = await db.Messages.AsNoTracking().Where(x => x.ConversationId == conversationId).OrderBy(x => x.CreatedAtUtc).ToListAsync(ct);
            var runRows = await db.Runs.AsNoTracking().Where(x => x.ConversationId == conversationId).OrderByDescending(x => x.CreatedAtUtc).ToListAsync(ct);
            var stepRows = await db.Steps.AsNoTracking().Where(x => x.ConversationId == conversationId).OrderBy(x => x.Seq).ThenBy(x => x.CreatedAtUtc).ToListAsync(ct);
            var artifactRows = await db.Artifacts.AsNoTracking().Where(x => x.ConversationId == conversationId).OrderBy(x => x.CreatedAtUtc).ToListAsync(ct);
            var messages = messageRows.Select(m => ToMessageDto(m, m.RunId.HasValue ? artifactRows.Where(a => a.RunId == m.RunId.Value).ToList() : null)).ToList();
            var runs = runRows.Select(r => ToRunDto(r, artifactRows.Where(a => a.RunId == r.Id).ToList(), stepRows.Where(st => st.RunId == r.Id).ToList())).ToList();
            return Microsoft.AspNetCore.Http.Results.Ok(new { conversation = ToConversationDto(c), messages, runs, artifacts = artifactRows.Select(ToArtifactDto).ToList() });
        });

        app.MapPost("/api/agent/conversations/{conversationId:guid}/messages", async (Guid conversationId, JsonElement payload, HttpRequest request, HttpContext http, IConfiguration cfg, AiDbContext db, IHttpClientFactory factory, IHubContext<AgentRealtimeHub> hub, CancellationToken ct) =>
        {
            var c = await GetConversationForUser(conversationId, http, cfg, db, asNoTracking: false);
            if (c == null) return Microsoft.AspNetCore.Http.Results.NotFound(new { message = "Диалог не найден.", code = "AI_CONVERSATION_NOT_FOUND" });

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
            var workerNotified = await NotifyAiWorkerAsync(factory, cfg, run.Id, "assistant-chat-turn-queued", app.Logger, ct);
            return Microsoft.AspNetCore.Http.Results.Ok(new { conversation = ToConversationDto(c), message = messageDto, run = runDto, queued = true, workerNotified });
        });

        app.MapPost("/api/agent/conversations/{conversationId:guid}/attachments", async (Guid conversationId, HttpRequest request, HttpContext http, IConfiguration cfg, AiDbContext db, CancellationToken ct) => await SaveAttachment(conversationId, request, http, cfg, db, ct)).DisableAntiforgery();

        app.MapPost("/api/agent/conversations/{conversationId:guid}/polish-task", async (Guid conversationId, JsonElement payload, HttpContext http, IConfiguration cfg, AiDbContext db, IHttpClientFactory factory, IHubContext<AgentRealtimeHub> hub, CancellationToken ct) => await QueueAgentRun(conversationId, "polish_assignment_draft", payload, http, cfg, db, factory, hub, app.Logger, ct));

        app.MapPost("/api/agent/conversations/{conversationId:guid}/polish-tasks", async (Guid conversationId, JsonElement payload, HttpContext http, IConfiguration cfg, AiDbContext db, IHttpClientFactory factory, IHubContext<AgentRealtimeHub> hub, CancellationToken ct) => await QueueAgentRun(conversationId, "polish_assignment_draft", payload, http, cfg, db, factory, hub, app.Logger, ct));

        app.MapPost("/api/agent/runs/{runId:guid}/cancel", async (Guid runId, HttpContext http, IConfiguration cfg, AiDbContext db, IHubContext<AgentRealtimeHub> hub, CancellationToken ct) =>
        {
            var run = await db.Runs.FindAsync(new object?[] { runId }, ct);
            if (run == null) return Microsoft.AspNetCore.Http.Results.NotFound(new { message = "Обработка не найдена.", code = "AI_RUN_NOT_FOUND" });
            var c = await GetConversationForUser(run.ConversationId, http, cfg, db, asNoTracking: true);
            if (c == null) return Microsoft.AspNetCore.Http.Results.Json(new { message = "Нет доступа к этой обработке.", code = "AI_RUN_FORBIDDEN" }, statusCode: 403);
            run.Status = "canceled";
            run.UpdatedAtUtc = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            await BroadcastAgentEventAsync(hub, run.ConversationId, "run.updated", new { run = ToRunDto(run), runId, status = "canceled" }, ct);
            return Microsoft.AspNetCore.Http.Results.Ok(new { runId, status = "canceled" });
        });

        app.MapPost("/api/agent/artifacts/{artifactId:guid}/apply", async (Guid artifactId, JsonElement payload, HttpRequest request, HttpContext http, IConfiguration cfg, AiDbContext db, IHttpClientFactory factory, CancellationToken ct) => await ApplyArtifact(null, artifactId, payload, request, http, cfg, db, factory, ct));

        app.MapPost("/api/agent/runs/{runId:guid}/artifacts/{artifactId:guid}/apply", async (Guid runId, Guid artifactId, JsonElement payload, HttpRequest request, HttpContext http, IConfiguration cfg, AiDbContext db, IHttpClientFactory factory, CancellationToken ct) => await ApplyArtifact(runId, artifactId, payload, request, http, cfg, db, factory, ct));

        app.MapGet("/api/agent/conversations/{conversationId:guid}/debug-dump", async (Guid conversationId, HttpContext http, IConfiguration cfg, AiDbContext db, CancellationToken ct) =>
        {
            var c = await GetConversationForUser(conversationId, http, cfg, db, asNoTracking: true);
            if (c == null) return Microsoft.AspNetCore.Http.Results.NotFound(new { message = "Диалог не найден.", code = "AI_CONVERSATION_NOT_FOUND" });

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

            return Microsoft.AspNetCore.Http.Results.Text(sb.ToString(), "text/plain");
        });

        return app;
    }
}
