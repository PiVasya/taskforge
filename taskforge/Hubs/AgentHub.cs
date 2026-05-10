using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using taskforge.Constants;
using taskforge.Data;

namespace taskforge.Hubs
{
    [Authorize(Roles = AppRoles.Admin + "," + AppRoles.Editor)]
    public sealed class AgentHub : Hub
    {
        public const string EventMethod = "AgentEvent";

        private readonly ApplicationDbContext _db;

        public AgentHub(ApplicationDbContext db)
        {
            _db = db;
        }

        public static string ConversationGroup(Guid conversationId) => $"agent-conversation-{conversationId:N}";

        public async Task JoinConversation(string conversationId)
        {
            if (Context.User?.IsInRole(AppRoles.Admin) != true && Context.User?.IsInRole(AppRoles.Editor) != true) return;
            if (!Guid.TryParse(conversationId, out var id)) return;
            var userId = GetUserId();
            if (userId == null) return;

            var ownsConversation = await _db.AgentConversations
                .AsNoTracking()
                .AnyAsync(x => x.Id == id && x.UserId == userId.Value);

            if (!ownsConversation) return;
            await Groups.AddToGroupAsync(Context.ConnectionId, ConversationGroup(id));
        }

        public async Task LeaveConversation(string conversationId)
        {
            if (!Guid.TryParse(conversationId, out var id)) return;
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, ConversationGroup(id));
        }

        private Guid? GetUserId()
        {
            var raw = Context.User?.FindFirstValue(ClaimTypes.NameIdentifier)
                      ?? Context.User?.FindFirstValue("sub");
            return Guid.TryParse(raw, out var id) ? id : null;
        }
    }
}
