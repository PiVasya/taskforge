using Microsoft.AspNetCore.SignalR;

namespace TaskForge.Minecraft.Api.Hubs;

public sealed class MinecraftChatHub : Hub
{
    public Task JoinChat() => Groups.AddToGroupAsync(Context.ConnectionId, "minecraft-chat");
    public Task LeaveChat() => Groups.RemoveFromGroupAsync(Context.ConnectionId, "minecraft-chat");
}
