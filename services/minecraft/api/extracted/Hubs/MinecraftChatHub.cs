using System;
using System.Linq;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using taskforge.Constants;

namespace taskforge.Hubs
{
    [Authorize]
    public sealed class MinecraftChatHub : Hub
    {
        public Task JoinChat()
        {
            var user = Context.User;
            if (user == null)
                throw new HubException("Unauthorized");

            var hasMinecraft = user.IsInRole(AppRoles.Admin)
                               || user.FindAll(ClaimTypes.Role).Any(c => string.Equals(c.Value, FeatureRoles.Minecraft, StringComparison.OrdinalIgnoreCase))
                               || user.FindAll("role").Any(c => string.Equals(c.Value, FeatureRoles.Minecraft, StringComparison.OrdinalIgnoreCase))
                               || user.FindAll("roles").Any(c => string.Equals(c.Value, FeatureRoles.Minecraft, StringComparison.OrdinalIgnoreCase));

            if (!hasMinecraft)
                throw new HubException("Forbidden");

            return Groups.AddToGroupAsync(Context.ConnectionId, "minecraft-chat");
        }

        public Task LeaveChat() => Groups.RemoveFromGroupAsync(Context.ConnectionId, "minecraft-chat");
    }
}
