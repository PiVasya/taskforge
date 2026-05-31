using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using TaskForge.AiAgent.Contracts;
using TaskForge.AiAgent.Options;

namespace TaskForge.AiAgent.Context;

public sealed class PromptContextComposer
{
    private readonly TaskForgeAgentOptions _options;
    private readonly AgentMemoryStore _memory;

    public PromptContextComposer(IOptions<TaskForgeAgentOptions> options, AgentMemoryStore memory)
    {
        _options = options.Value;
        _memory = memory;
    }

    public string ComposeRunPrompt(ClaimedAgentJob job, string workflowName)
    {
        MergeBackendMemory(job);
        var payload = CompactJson(job.Payload, _options.MaxContextCharacters);
        var memory = _memory.Get(job.ConversationId).ToJsonString(new JsonSerializerOptions { WriteIndented = false });

        return $$"""
TaskForge .NET Agent run
workflow: {{workflowName}}
runId: {{job.RunId}}
conversationId: {{job.ConversationId}}
jobType: {{job.JobType}}
courseId: {{job.CourseId?.ToString() ?? "null"}}
courseTitle: {{job.CourseTitle ?? "null"}}
userText: {{job.UserText}}

ConversationMemory:
{{memory}}

BackendPayload:
{{payload}}
""";
    }

    private void MergeBackendMemory(ClaimedAgentJob job)
    {
        try
        {
            var memoryElement = job.Payload.GetPropertyOrDefault("memory");
            if (memoryElement.ValueKind != JsonValueKind.Object)
                return;

            var node = JsonNode.Parse(memoryElement.GetRawText()) as JsonObject;
            if (node != null)
                _memory.Merge(job.ConversationId, node);
        }
        catch
        {
            // Backend payload memory is a convenience cache. A malformed value must not break the run.
        }
    }

    public static string CompactJson(JsonElement element, int maxCharacters)
    {
        var raw = element.GetRawText();
        if (raw.Length <= maxCharacters)
            return raw;
        return raw[..maxCharacters] + "\n/* truncated by PromptContextComposer */";
    }
}
