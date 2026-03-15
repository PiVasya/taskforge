using System;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using taskforge.Data;
using taskforge.Extensions;

namespace taskforge.Hubs
{
    /// <summary>
    /// SignalR хаб для оповещений службы поддержки.
    /// Позволяет подписывать клиентов на конкретные тикеты или пользовательские группы.
    /// </summary>
    [Authorize]
    public class SupportHub : Hub
    {
        private readonly ApplicationDbContext _db;

        public SupportHub(ApplicationDbContext db)
        {
            _db = db;
        }
        /// <summary>
        /// Присоединяет текущего пользователя к группе его собственных тикетов.
        /// </summary>
        public Task JoinUser()
        {
            var userId = Context.User?.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!string.IsNullOrEmpty(userId))
            {
                return Groups.AddToGroupAsync(Context.ConnectionId, $"user-{userId}");
            }
            return Task.CompletedTask;
        }

        /// <summary>
        /// Присоединяет текущего клиента к группе конкретного тикета.
        /// </summary>
        /// <param name="ticketId">Идентификатор тикета</param>
        public async Task JoinTicket(Guid ticketId)
        {
            var userIdStr = Context.User?.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(userIdStr) || !Guid.TryParse(userIdStr, out var userId))
                throw new HubException("Unauthorized");

            var isSupportAdmin = Context.User != null && UserPermissions.IsSupportAdmin(Context.User);
            var allowed = isSupportAdmin || await _db.SupportTickets.AsNoTracking().AnyAsync(t => t.Id == ticketId && t.UserId == userId);
            if (!allowed)
                throw new HubException("Forbidden");

            await Groups.AddToGroupAsync(Context.ConnectionId, $"ticket-{ticketId}");
        }

        /// <summary>
        /// Отсоединяет клиента от группы конкретного тикета.
        /// </summary>
        /// <param name="ticketId">Идентификатор тикета</param>
        public Task LeaveTicket(Guid ticketId)
        {
            return Groups.RemoveFromGroupAsync(Context.ConnectionId, $"ticket-{ticketId}");
        }

        /// <summary>
        /// Присоединяет администраторского клиента в общую группу админов.
        /// </summary>
        public Task JoinAdmins()
        {
            if (Context.User == null || !UserPermissions.IsSupportAdmin(Context.User))
                throw new HubException("Forbidden");

            return Groups.AddToGroupAsync(Context.ConnectionId, "support-admins");
        }
    }
}