using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;

namespace TaskForge.AiAgent.Llm;

public sealed class AgentSessionStore
{
    private readonly ConcurrentDictionary<Guid, JsonElement> _sessions = new();
    private readonly ILogger<AgentSessionStore> _logger;

    public AgentSessionStore(ILogger<AgentSessionStore> logger) => _logger = logger;

    public async Task<AgentSession> LoadAsync(AIAgent agent, Guid conversationId, CancellationToken cancellationToken)
    {
        if (_sessions.TryGetValue(conversationId, out var state))
        {
            try
            {
                return await agent.DeserializeSessionAsync(state, cancellationToken: cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to deserialize agent session for conversation {ConversationId}; starting a new one.", conversationId);
            }
        }

        return await agent.CreateSessionAsync(cancellationToken);
    }

    public async Task SaveAsync(AIAgent agent, AgentSession session, Guid conversationId, CancellationToken cancellationToken)
    {
        var state = await agent.SerializeSessionAsync(session, cancellationToken: cancellationToken);
        _sessions[conversationId] = state;
    }
}
