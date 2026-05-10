using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace TaskForge.AiAgent.Context;

public sealed class AgentMemoryStore
{
    private readonly ConcurrentDictionary<Guid, JsonObject> _memory = new();

    public JsonObject Get(Guid conversationId)
        => _memory.GetOrAdd(conversationId, _ => new JsonObject());

    public void Merge(Guid conversationId, JsonObject patch)
    {
        var target = Get(conversationId);
        foreach (var kvp in patch)
            target[kvp.Key] = kvp.Value?.DeepClone();
    }
}
