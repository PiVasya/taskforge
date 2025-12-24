using System;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace taskforge.Hubs
{
    /// <summary>
    /// SignalR хаб для оповещений службы поддержки.
    /// Позволяет подписывать клиентов на конкретные тикеты или пользовательские группы.
    /// </summary>
    [Authorize]
    public class SupportHub : Hub
    {
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
        public Task JoinTicket(Guid ticketId)
        {
            return Groups.AddToGroupAsync(Context.ConnectionId, $"ticket-{ticketId}");
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
            return Groups.AddToGroupAsync(Context.ConnectionId, "support-admins");
        }
    }
}