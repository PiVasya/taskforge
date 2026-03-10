using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace taskforge.Hubs
{
    [Authorize]
    public sealed class MinecraftChatHub : Hub
    {
        public Task JoinChat() => Groups.AddToGroupAsync(Context.ConnectionId, "minecraft-chat");
        public Task LeaveChat() => Groups.RemoveFromGroupAsync(Context.ConnectionId, "minecraft-chat");
    }
}
