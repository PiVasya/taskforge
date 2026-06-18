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
    private static WebApplication MapWorkerInternalEndpoints(WebApplication app)
    {
        app.MapPost("/api/internal/agent/claim-next", async (AgentClaimNextRequest request, AiDbContext db, IHubContext<AgentRealtimeHub> hub, CancellationToken ct) =>
        {
            var run = await db.Runs.Where(x => x.Status == "queued").OrderBy(x => x.CreatedAtUtc).FirstOrDefaultAsync(ct);
            if (run == null) return Microsoft.AspNetCore.Http.Results.Ok(new { ok = true, request.WorkerId, job = (object?)null });
            run.Status = "running";
            run.WorkerId = request.WorkerId;
            run.StartedAtUtc = DateTimeOffset.UtcNow;
            run.UpdatedAtUtc = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            await BroadcastAgentEventAsync(hub, run.ConversationId, "run.updated", new { run = ToRunDto(run), runId = run.Id, status = run.Status, workerId = request.WorkerId }, ct);
            var payload = ParseJson(run.PayloadJson) ?? new { conversationId = run.ConversationId, rawText = "" };
            return Microsoft.AspNetCore.Http.Results.Ok(new { ok = true, request.WorkerId, job = new { id = run.Id, runId = run.Id, run.ConversationId, jobType = run.JobType, type = run.JobType, payload } });
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
            return Microsoft.AspNetCore.Http.Results.Ok(new { ok = true, runId, request.WorkerId });
        });

        app.MapPost("/api/internal/agent/runs/{runId:guid}/steps", async (Guid runId, AgentStepRequest request, AiDbContext db, IHubContext<AgentRealtimeHub> hub, CancellationToken ct) =>
        {
            var run = await db.Runs.FindAsync(new object?[] { runId }, ct);
            if (run == null) return Microsoft.AspNetCore.Http.Results.NotFound(new { message = "Обработка не найдена.", code = "AI_RUN_NOT_FOUND" });

            var nextSeq = (await db.Steps.Where(x => x.RunId == runId).Select(x => (int?)x.Seq).MaxAsync(ct) ?? 0) + 1;
            var step = BuildStep(run, request, nextSeq);
            db.Steps.Add(step);
            run.UpdatedAtUtc = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);

            var stepDto = ToStepDto(step);
            await BroadcastAgentEventAsync(hub, run.ConversationId, "step.created", new { runId, step = stepDto }, ct);
            return Microsoft.AspNetCore.Http.Results.Ok(new { ok = true, runId, request.WorkerId, step = stepDto });
        });

        app.MapPost("/api/internal/agent/runs/{runId:guid}/complete", async (Guid runId, AgentCompleteRequest request, AiDbContext db, IHubContext<AgentRealtimeHub> hub, CancellationToken ct) =>
        {
            var run = await db.Runs.FindAsync(new object?[] { runId }, ct);
            if (run == null) return Microsoft.AspNetCore.Http.Results.NotFound(new { message = "Обработка не найдена.", code = "AI_RUN_NOT_FOUND" });
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
            return Microsoft.AspNetCore.Http.Results.Ok(new { ok = true, runId, request.WorkerId, status = "completed" });
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
            return Microsoft.AspNetCore.Http.Results.Ok(new { ok = true, runId, request.WorkerId, status = "failed" });
        });

        app.MapPost("/api/internal/agent/tools/run-tests", async (AgentRunTestsRequest request, IHttpClientFactory factory, IConfiguration cfg, CancellationToken ct) => await RunTestsBridge(request, factory, cfg, ct));

        return app;
    }
}
