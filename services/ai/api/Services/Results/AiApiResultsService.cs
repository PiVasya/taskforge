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
using static TaskForge.Ai.Api.Services.Serialization.AiApiSerializationService;
using static TaskForge.Ai.Api.Services.Testing.AiApiTestingService;

namespace TaskForge.Ai.Api.Services.Results;

internal static class AiApiResultsService
{
    internal static async Task<IResult> QueueAgentRun(Guid conversationId, string jobType, JsonElement payload, HttpContext http, IConfiguration cfg, AiDbContext db, IHubContext<AgentRealtimeHub> hub, CancellationToken ct)
    {
        var c = await GetConversationForUser(conversationId, http, cfg, db, asNoTracking: false);
        if (c == null) return Microsoft.AspNetCore.Http.Results.NotFound(new { message = "Диалог не найден.", code = "AI_CONVERSATION_NOT_FOUND" });
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
        return Microsoft.AspNetCore.Http.Results.Ok(new { run = runDto, queued = true });
    }

    internal static async Task<JsonObject> BuildRunPayloadAsync(
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

    internal static object ToRunDto(AiRun x, IReadOnlyList<AiArtifact>? artifacts = null, IReadOnlyList<AiStep>? steps = null) => new
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

}
