using System.Text.Json;
using System.Text.Json.Nodes;
using TaskForge.AiAgent.Contracts;
using TaskForge.AiAgent.Context;
using TaskForge.AiAgent.Workflows;

namespace TaskForge.AiAgent.Runtime;

public sealed class ResultEnvelopeBuilder
{
    private readonly AgentMemoryStore _memoryStore;
    public ResultEnvelopeBuilder(AgentMemoryStore memoryStore) => _memoryStore = memoryStore;

    public AgentResultEnvelope FromWorkflowState(WorkflowState state)
    {
        var envelope = new AgentResultEnvelope
        {
            Status = state.RequiresApproval ? "completed_waiting_approval" : "completed",
            ScenarioId = state.ScenarioId,
            AssistantMessage = string.IsNullOrWhiteSpace(state.AssistantMessage)
                ? "Готово. Я подготовил результат."
                : state.AssistantMessage,
            MemoryPatch = new JsonObject
            {
                ["lastIntent"] = state.WorkflowName,
                ["activeCourseId"] = state.Job.CourseId?.ToString(),
                ["lastRunId"] = state.Job.RunId.ToString(),
                ["updatedAtUtc"] = DateTime.UtcNow.ToString("O")
            }
        };

        foreach (var artifact in state.Artifacts)
            envelope.Artifacts.Add(artifact);

        envelope.Debug["workflow"] = state.WorkflowName;
        envelope.Debug["notes"] = new JsonArray(state.Notes.Select(x => JsonValue.Create(x)).ToArray<JsonNode?>());
        _memoryStore.Merge(state.Job.ConversationId, envelope.MemoryPatch);
        return envelope;
    }

    public static AgentResultEnvelope FromRawAgentText(string text, ClaimedAgentJob job, string scenarioId)
    {
        var clean = ExtractJson(text);
        if (clean != null)
        {
            try
            {
                var node = JsonNode.Parse(clean)?.AsObject();
                if (node != null)
                    return FromJsonObject(node, job, scenarioId);
            }
            catch
            {
                // Fall through to text answer.
            }
        }

        return new AgentResultEnvelope
        {
            Status = "completed",
            ScenarioId = scenarioId,
            AssistantMessage = string.IsNullOrWhiteSpace(text) ? "Готово." : text.Trim(),
            MemoryPatch = new JsonObject
            {
                ["lastIntent"] = scenarioId,
                ["activeCourseId"] = job.CourseId?.ToString(),
                ["lastRunId"] = job.RunId.ToString()
            }
        };
    }

    private static AgentResultEnvelope FromJsonObject(JsonObject node, ClaimedAgentJob job, string fallbackScenarioId)
    {
        var envelope = new AgentResultEnvelope
        {
            Status = node["status"]?.ToString() ?? "completed",
            ScenarioId = node["scenarioId"]?.ToString() ?? fallbackScenarioId,
            AssistantMessage = node["assistantMessage"]?.ToString() ?? node["assistant_message"]?.ToString() ?? "Готово.",
            MemoryPatch = node["memoryPatch"] as JsonObject ?? new JsonObject
            {
                ["lastIntent"] = fallbackScenarioId,
                ["activeCourseId"] = job.CourseId?.ToString()
            }
        };

        if (node["artifacts"] is JsonArray artifacts)
        {
            foreach (var item in artifacts.OfType<JsonObject>())
            {
                envelope.Artifacts.Add(new AgentArtifact(
                    item["type"]?.ToString() ?? "artifact",
                    item["title"]?.ToString() ?? item["type"]?.ToString() ?? "artifact",
                    item["data"]?.DeepClone() ?? new JsonObject()));
            }
        }

        return envelope;
    }

    private static string? ExtractJson(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var trimmed = text.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewLine = trimmed.IndexOf('\n');
            var lastFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            if (firstNewLine >= 0 && lastFence > firstNewLine)
                trimmed = trimmed[(firstNewLine + 1)..lastFence].Trim();
        }
        return trimmed.StartsWith('{') && trimmed.EndsWith('}') ? trimmed : null;
    }
}
