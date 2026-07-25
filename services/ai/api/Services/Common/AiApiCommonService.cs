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
using static TaskForge.Ai.Api.Services.Mapping.AiApiMappingService;
using static TaskForge.Ai.Api.Services.Results.AiApiResultsService;
using static TaskForge.Ai.Api.Services.Serialization.AiApiSerializationService;
using static TaskForge.Ai.Api.Services.Testing.AiApiTestingService;

namespace TaskForge.Ai.Api.Services.Common;

internal static class AiApiCommonService
{
    internal static void AddInternalKey(HttpRequestMessage msg, IConfiguration cfg)
    {
        var key = cfg["InternalApi:Key"] ?? cfg["TaskForge:InternalKey"] ?? Environment.GetEnvironmentVariable("TASKFORGE_INTERNAL_KEY") ?? Environment.GetEnvironmentVariable("TASKFORGE_AGENT_INTERNAL_KEY");
        if (!string.IsNullOrWhiteSpace(key)) msg.Headers.TryAddWithoutValidation("X-Internal-Key", key);
    }


    internal static async Task<bool> NotifyAiWorkerAsync(
        IHttpClientFactory httpFactory,
        IConfiguration cfg,
        Guid runId,
        string reason,
        ILogger logger,
        CancellationToken ct)
    {
        var baseUrl = (cfg["AiWorker:BaseUrl"] ?? "http://ai-worker:8080").TrimEnd('/');
        var timeoutSeconds = System.Math.Clamp(cfg.GetValue("AiWorker:WakeTimeoutSeconds", 5), 1, 30);
        var attempts = System.Math.Clamp(cfg.GetValue("AiWorker:WakeAttempts", 3), 1, 5);

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                var client = httpFactory.CreateClient();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
                using var message = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/internal/wake")
                {
                    Content = JsonContent.Create(new
                    {
                        reason,
                        runId,
                        requestedAtUtc = DateTimeOffset.UtcNow
                    })
                };
                AddInternalKey(message, cfg);
                using var response = await client.SendAsync(message, timeout.Token);
                if (response.IsSuccessStatusCode)
                {
                    logger.LogInformation(
                        "AI worker wake accepted: runId={RunId} reason={Reason} attempt={Attempt}",
                        runId, reason, attempt);
                    return true;
                }

                var body = await response.Content.ReadAsStringAsync(timeout.Token);
                logger.LogWarning(
                    "AI worker wake rejected: runId={RunId} reason={Reason} attempt={Attempt}/{Attempts} status={Status} body={Body}",
                    runId, reason, attempt, attempts, (int)response.StatusCode, body);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(
                    "AI worker wake timed out: runId={RunId} reason={Reason} attempt={Attempt}/{Attempts}",
                    runId, reason, attempt, attempts);
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "AI worker wake failed: runId={RunId} reason={Reason} attempt={Attempt}/{Attempts}",
                    runId, reason, attempt, attempts);
            }

            if (attempt < attempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(150 * attempt), ct);
            }
        }

        logger.LogError(
            "AI worker was not notified after {Attempts} attempts. Run remains queued and will be drained on the next wake or worker restart: runId={RunId}",
            attempts, runId);
        return false;
    }

    internal static JsonArray SelectAssignments(JsonNode? assignments, Guid assignmentId)
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

    internal static int CountAssignments(JsonNode? assignments)
    {
        var array = ExtractArray(assignments);
        return array?.Count ?? 0;
    }

    internal static async Task BroadcastAgentEventAsync(IHubContext<AgentRealtimeHub> hub, Guid conversationId, string type, object payload, CancellationToken ct)
    {
        await hub.Clients.Group(AgentRealtimeHub.ConversationGroup(conversationId)).SendAsync("AgentEvent", new
        {
            type,
            conversationId,
            payload
        }, ct);
    }

}
