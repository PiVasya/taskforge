using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using TaskForge.Ai.Api.Data;
using TaskForge.Ai.Api.Domain;


namespace TaskForge.Ai.Api.Hubs;

public sealed class AgentRealtimeHub : Hub
{
    public static string ConversationGroup(Guid conversationId) => $"agent-conversation-{conversationId:N}";

    public static string ConversationGroup(string conversationId)
        => Guid.TryParse(conversationId, out var parsed)
            ? ConversationGroup(parsed)
            : $"agent-conversation-{conversationId}";

    public Task JoinConversation(string conversationId)
        => Groups.AddToGroupAsync(Context.ConnectionId, ConversationGroup(conversationId));

    public Task LeaveConversation(string conversationId)
        => Groups.RemoveFromGroupAsync(Context.ConnectionId, ConversationGroup(conversationId));
}
