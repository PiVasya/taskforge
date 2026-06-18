using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using TaskForge.AiAgent.Context;
using TaskForge.AiAgent.Contracts;
using TaskForge.AiAgent.Llm;
using TaskForge.AiAgent.Options;
using TaskForge.AiAgent.Runtime;

namespace TaskForge.AiAgent.Workflows.AgentLoop;

public sealed partial class AdaptiveAgentLoopWorkflow
{
    private JsonObject InspectContext(AgentLoopState state)
    {
        var payload = state.Job.Payload;
        var memory = state.WorkingMemory;
        memory["contextLoadedAtUtc"] = DateTimeOffset.UtcNow.ToString("O");
        memory["contextPrompt"] = Trim(_contextComposer.ComposeRunPrompt(state.Job, Name), _options.MaxContextCharacters);
        memory["payloadKeys"] = new JsonArray(GetPayloadKeys(payload).Select(x => JsonValue.Create(x)).ToArray<JsonNode?>());
        memory["courseDigest"] = ClonePayloadProperty(payload, "courseDigest") ?? new JsonObject
        {
            ["selectedCourseId"] = state.Job.CourseId?.ToString(),
            ["selectedAssignmentId"] = state.Job.AssignmentId?.ToString()
        };
        memory["assignments"] = ClonePayloadProperty(payload, "assignments") ?? new JsonArray();
        memory["courseOutline"] = ClonePayloadProperty(payload, "courseOutline") ?? new JsonArray();
        memory["targetAssignments"] = ClonePayloadProperty(payload, "targetAssignments") ?? new JsonArray();
        memory["focusAssignments"] = ClonePayloadProperty(payload, "focusAssignments") ?? new JsonArray();
        memory["recentMessages"] = ClonePayloadProperty(payload, "recentMessages") ?? new JsonArray();
        memory["conversationState"] = ClonePayloadProperty(payload, "conversationState") ?? new JsonObject();
        memory["contextWarnings"] = ClonePayloadProperty(payload, "contextWarnings") ?? new JsonArray();
        memory["rawUserRequest"] = state.Job.UserText;
        state.LoadedContext = true;
        state.Notes.Add("Context inspected and copied into shared working memory.");

        return new JsonObject
        {
            ["ok"] = true,
            ["loadedContext"] = true,
            ["payloadKeys"] = memory["payloadKeys"]?.DeepClone(),
            ["courseDigest"] = memory["courseDigest"]?.DeepClone(),
            ["conversationStateAvailable"] = memory["conversationState"] is JsonObject { Count: > 0 },
            ["summary"] = "Контекст запроса, чата, курса и заданий сохранён в общей памяти run-а."
        };
    }

    private JsonObject ClassifyRequest(AgentLoopState state)
    {
        var intent = AgentIntentClassifier.Select(state.Job);
        var text = state.Job.UserText.ToLowerInvariant();
        var needsCourse = text.Contains("курс") || text.Contains("в стиле") || text.Contains("как в курсе") || text.Contains("пробел") || text.Contains("скач");
        var needsDrafts = intent.IsDraftScenario || LooksLikeDraftRequest(text);
        var intentJson = intent.ToJsonObject();
        intentJson["needsCourseContext"] = needsCourse;
        intentJson["needsAssignmentDrafts"] = needsDrafts;
        intentJson["detectedAtUtc"] = DateTimeOffset.UtcNow.ToString("O");
        state.WorkingMemory["intent"] = intentJson.DeepClone();
        state.ScenarioId = intent.ScenarioId == "free_chat" && needsDrafts ? "style_matched_tasks" : intent.ScenarioId;
        state.Notes.Add($"Request classified as {state.ScenarioId}.");

        return new JsonObject
        {
            ["ok"] = true,
            ["intent"] = intentJson,
            ["summary"] = $"Определён сценарий: {state.ScenarioId}."
        };
    }

    private async Task<JsonObject> DelegateWorkflowAsync(AgentLoopState state, ITaskForgeWorkflow workflow, string workflowName, CancellationToken cancellationToken)
    {
        state.SelectedWorkflow = workflowName;
        state.Notes.Add($"Delegating to {workflowName}.");
        await _steps.TryReportAsync(
            "agent_delegate",
            "running",
            $"Передаю работу сценарию: {GetWorkflowTitle(workflowName)}",
            "Передача идёт не вслепую: вся рабочая память и уже собранный контекст сохранены в логе run-а.",
            new JsonObject
            {
                ["workflow"] = workflowName,
                ["state"] = state.ToJsonObject(_options.MaxAgentStateCharacters)
            });

        var delegatedJob = BuildDelegatedJob(state, workflowName);
        var result = await workflow.RunAsync(delegatedJob, cancellationToken);
        state.DelegatedResult = result;
        state.FinalMessage = result.AssistantMessage;
        state.ScenarioId = string.IsNullOrWhiteSpace(result.ScenarioId) ? state.ScenarioId : result.ScenarioId;
        state.Notes.Add($"Workflow {workflowName} returned a result. Model will receive the observation and decide whether to finish.");

        return new JsonObject
        {
            ["ok"] = true,
            ["workflow"] = workflowName,
            ["scenarioId"] = result.ScenarioId,
            ["assistantMessagePreview"] = Trim(result.AssistantMessage, 1000),
            ["artifactCount"] = result.Artifacts.Count,
            ["summary"] = $"Сценарий {GetWorkflowTitle(workflowName)} завершён, результат сохранён в памяти agent loop."
        };
    }


    private ClaimedAgentJob BuildDelegatedJob(AgentLoopState state, string workflowName)
    {
        var payload = ToMutablePayload(state.Job.Payload);
        var traceSnapshot = new JsonArray(state.Trace.Select(x => x.ToJsonObject()).ToArray<JsonNode?>());
        var noteSnapshot = new JsonArray(state.Notes.Select(x => JsonValue.Create(x)).ToArray<JsonNode?>());
        var agentLoopMemory = new JsonObject
        {
            ["enabled"] = true,
            ["sourceWorkflow"] = Name,
            ["targetWorkflow"] = workflowName,
            ["stepNumber"] = state.StepNumber,
            ["scenarioId"] = state.ScenarioId,
            ["selectedWorkflow"] = state.SelectedWorkflow,
            ["workingMemory"] = state.WorkingMemory.DeepClone(),
            ["trace"] = traceSnapshot.DeepClone(),
            ["notes"] = noteSnapshot.DeepClone(),
            ["createdAtUtc"] = DateTimeOffset.UtcNow.ToString("O")
        };

        payload["agentLoopMemory"] = agentLoopMemory.DeepClone();
        CopyWorkingMemoryNode(payload, state, "courseMap");
        CopyWorkingMemoryNode(payload, state, "courseStyleProfile");
        CopyWorkingMemoryNode(payload, state, "courseGapReport");
        CopyWorkingMemoryNode(payload, state, "assignmentComplexityReport");
        CopyWorkingMemoryNode(payload, state, "pendingPatchSet");
        CopyWorkingMemoryNode(payload, state, "courseEnrichmentBrief");
        CopyWorkingMemoryNode(payload, state, "delegatedResultReview");

        var memory = payload["memory"] as JsonObject ?? new JsonObject();
        memory["agentLoop"] = new JsonObject
        {
            ["scenarioId"] = state.ScenarioId,
            ["selectedWorkflow"] = workflowName,
            ["workingMemory"] = state.WorkingMemory.DeepClone(),
            ["lastTrace"] = traceSnapshot.DeepClone(),
            ["notes"] = noteSnapshot.DeepClone()
        };
        payload["memory"] = memory;

        var enrichedPayload = ToJsonElement(payload);
        return state.Job with { Payload = enrichedPayload };
    }

    private static void CopyWorkingMemoryNode(JsonObject payload, AgentLoopState state, string key)
    {
        if (state.WorkingMemory.TryGetPropertyValue(key, out var node) && node is not null)
            payload[key] = node.DeepClone();
    }

    private static JsonObject ToMutablePayload(JsonElement payload)
    {
        try
        {
            if (payload.ValueKind == JsonValueKind.Object && JsonNode.Parse(payload.GetRawText()) is JsonObject obj)
                return obj.DeepClone().AsObject();
        }
        catch
        {
            // Fall through to wrapper below.
        }

        return new JsonObject
        {
            ["rawPayload"] = payload.ValueKind == JsonValueKind.Undefined ? null : payload.GetRawText()
        };
    }

    private static JsonElement ToJsonElement(JsonObject payload)
    {
        using var document = JsonDocument.Parse(payload.ToJsonString());
        return document.RootElement.Clone();
    }

}
