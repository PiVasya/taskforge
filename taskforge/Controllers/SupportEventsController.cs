using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.Authorization;
using taskforge.Data;
using taskforge.Data.Models.Entities;
using taskforge.Hubs;

namespace taskforge.Controllers
{
    /// <summary>
    /// Контроллер для внутренних событий поддержки.
    /// Используется ботом для записи ответов администратора.
    /// </summary>
    [ApiController]
    [Route("api/support/events")]
    public class SupportEventsController : ControllerBase
    {
        private readonly ApplicationDbContext _db;
        private readonly IHubContext<SupportHub> _hub;
        private readonly IConfiguration _config;

        public SupportEventsController(ApplicationDbContext db, IHubContext<SupportHub> hub, IConfiguration config)
        {
            _db = db;
            _hub = hub;
            _config = config;
        }

        /// <summary>
        /// Обработать новое сообщение от админа из внешнего канала (например, Telegram).
        /// Проверяет internal key в заголовке X-Internal-Key.
        /// </summary>
        [HttpPost("new")]
        [AllowAnonymous]
        public async Task<IActionResult> New([FromBody] NewEventRequest request)
        {
            var expectedKey = _config["API_INTERNAL_KEY"];
            var header = Request.Headers["X-Internal-Key"].ToString();
            if (string.IsNullOrEmpty(expectedKey) || header != expectedKey)
            {
                return Unauthorized();
            }
            var ticket = await _db.SupportTickets.Include(t => t.User).FirstOrDefaultAsync(t => t.Id == request.TicketId);
            if (ticket == null) return NotFound();
            var msg = new SupportMessage
            {
                TicketId = request.TicketId,
                Text = request.Message.Trim(),
                CreatedAt = DateTime.UtcNow,
                IsFromAdmin = true,
                AuthorName = request.AuthorName,
                Source = "TelegramAdmin"
            };
            _db.SupportMessages.Add(msg);
            ticket.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            // отправляем обновление пользователю и админам
            var payload = new
            {
                msg.Id,
                msg.Text,
                msg.CreatedAt,
                msg.IsFromAdmin,
                AuthorName = msg.AuthorName ?? "Админ"
            };
            await _hub.Clients.Group($"user-{ticket.UserId}").SendAsync("ReceiveMessage", ticket.Id, payload);
            await _hub.Clients.Group("support-admins").SendAsync("ReceiveMessage", ticket.Id, payload);
            await _hub.Clients.Group($"ticket-{ticket.Id}").SendAsync("ReceiveMessage", ticket.Id, payload);
            return Ok();
        }

        /// <summary>
        /// DTO для передачи нового сообщения от бота.
        /// </summary>
        public class NewEventRequest
        {
            public Guid TicketId { get; set; }
            public string AuthorName { get; set; } = string.Empty;
            public string Message { get; set; } = string.Empty;
        }
    }
}