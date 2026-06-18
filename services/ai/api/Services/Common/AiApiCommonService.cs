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
