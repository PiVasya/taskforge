using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.SignalR;
using taskforge.Data;
using taskforge.Data.Models.Entities;
using taskforge.Hubs;

namespace taskforge.Controllers
{
    /// <summary>
    /// Контроллер для работы с обращениями в поддержку.
    /// </summary>
    [ApiController]
    [Route("api/support")]
    [Authorize]
    public class SupportController : ControllerBase
    {
        private readonly ApplicationDbContext _db;
        private readonly IHubContext<SupportHub> _hub;

        public SupportController(ApplicationDbContext db, IHubContext<SupportHub> hub)
        {
            _db = db;
            _hub = hub;
        }

        [HttpGet]
        public async Task<IActionResult> GetTickets()
        {
            var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (userIdStr == null) return Unauthorized();
            var userId = Guid.Parse(userIdStr);
            bool isAdmin = User.IsInRole("Admin") || User.IsInRole("Instructor") || User.HasClaim("canEdit", "true");

            var query = _db.SupportTickets
                .Include(t => t.Messages)
                .AsQueryable();

            if (!isAdmin)
            {
                query = query.Where(t => t.UserId == userId);
            }

            var tickets = await query
                .OrderByDescending(t => t.UpdatedAt)
                .Select(t => new
                {
                    t.Id,
                    t.Type,
                    t.IsClosed,
                    t.CreatedAt,
                    t.UpdatedAt,
                    MessagesCount = t.Messages.Count
                }).ToListAsync();

            return Ok(tickets);
        }

        [HttpGet("{id:guid}")]
        public async Task<IActionResult> GetTicket(Guid id)
        {
            var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (userIdStr == null) return Unauthorized();
            var userId = Guid.Parse(userIdStr);
            bool isAdmin = User.IsInRole("Admin") || User.IsInRole("Instructor") || User.HasClaim("canEdit", "true");

            // Убираем OrderBy из Include — сортировку сделаем после загрузки
            var ticket = await _db.SupportTickets
                .Include(t => t.Messages)
                .Include(t => t.User)
                .FirstOrDefaultAsync(t => t.Id == id);

            if (ticket == null) return NotFound();
            if (!isAdmin && ticket.UserId != userId) return Forbid();

            var result = new
            {
                ticket.Id,
                ticket.Type,
                ticket.CreatedAt,
                ticket.UpdatedAt,
                ticket.IsClosed,
                Messages = ticket.Messages
                    .OrderBy(m => m.CreatedAt)
                    .Select(m => new
                    {
                        m.Id,
                        m.Text,
                        m.CreatedAt,
                        m.IsFromAdmin,
                        AuthorName = m.IsFromAdmin
                            ? (m.AuthorName ?? "Админ")
                            : ($"{ticket.User.FirstName} {ticket.User.LastName}")
                    })
                    .ToList()
            };

            return Ok(result);
        }

        [HttpPost]
        public async Task<IActionResult> Create([FromBody] CreateRequest request)
        {
            var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (userIdStr == null) return Unauthorized();
            var userId = Guid.Parse(userIdStr);
            if (string.IsNullOrWhiteSpace(request.Message))
            {
                return BadRequest(new { message = "Сообщение не может быть пустым" });
            }

            var type = string.IsNullOrWhiteSpace(request.Type) ? "question" : request.Type.Trim();

            var ticket = new SupportTicket
            {
                UserId = userId,
                Type = type,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                IsClosed = false
            };

            var message = new SupportMessage
            {
                Ticket = ticket,
                AuthorUserId = userId,
                Text = request.Message.Trim(),
                CreatedAt = DateTime.UtcNow,
                IsFromAdmin = false,
                Source = "SiteUser"
            };

            _db.SupportTickets.Add(ticket);
            _db.SupportMessages.Add(message);
            await _db.SaveChangesAsync();

            var author = await _db.Users.FindAsync(userId);
            var authorName = author != null
                ? $"{author.FirstName} {author.LastName}"
                : string.Empty;

            await _hub.Clients.Group($"user-{userId}")
                .SendAsync("ReceiveMessage", ticket.Id, new
                {
                    message.Id,
                    message.Text,
                    message.CreatedAt,
                    message.IsFromAdmin,
                    AuthorName = authorName
                });

            await _hub.Clients.Group("support-admins")
                .SendAsync("ReceiveMessage", ticket.Id, new
                {
                    message.Id,
                    message.Text,
                    message.CreatedAt,
                    message.IsFromAdmin,
                    AuthorName = authorName
                });

            return Ok(new { ticket.Id });
        }

        [HttpPost("{id:guid}")]
        public async Task<IActionResult> AddMessage(Guid id, [FromBody] AddMessageRequest request)
        {
            var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (userIdStr == null) return Unauthorized();
            var userId = Guid.Parse(userIdStr);

            var ticket = await _db.SupportTickets
                .Include(t => t.User)
                .FirstOrDefaultAsync(t => t.Id == id);
            if (ticket == null) return NotFound();

            bool isAdmin = User.IsInRole("Admin") || User.IsInRole("Instructor") || User.HasClaim("canEdit", "true");
            if (!isAdmin && ticket.UserId != userId) return Forbid();

            if (string.IsNullOrWhiteSpace(request.Message))
            {
                return BadRequest(new { message = "Сообщение не может быть пустым" });
            }

            var msg = new SupportMessage
            {
                TicketId = ticket.Id,
                AuthorUserId = isAdmin ? (Guid?)null : userId,
                AuthorName = isAdmin ? User.Identity?.Name : null,
                Text = request.Message.Trim(),
                CreatedAt = DateTime.UtcNow,
                IsFromAdmin = isAdmin,
                Source = isAdmin ? "SiteAdmin" : "SiteUser"
            };

            _db.SupportMessages.Add(msg);
            ticket.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();

            var payload = new
            {
                msg.Id,
                msg.Text,
                msg.CreatedAt,
                msg.IsFromAdmin,
                AuthorName = msg.IsFromAdmin
                    ? (msg.AuthorName ?? "Админ")
                    : $"{ticket.User.FirstName} {ticket.User.LastName}"
            };

            await _hub.Clients.Group($"user-{ticket.UserId}")
                .SendAsync("ReceiveMessage", ticket.Id, payload);
            await _hub.Clients.Group("support-admins")
                .SendAsync("ReceiveMessage", ticket.Id, payload);
            await _hub.Clients.Group($"ticket-{ticket.Id}")
                .SendAsync("ReceiveMessage", ticket.Id, payload);

            return Ok(new { msg.Id });
        }

        [HttpPost("{id:guid}/close")]
        public async Task<IActionResult> CloseTicket(Guid id)
        {
            bool isAdmin = User.IsInRole("Admin") || User.IsInRole("Instructor") || User.HasClaim("canEdit", "true");
            if (!isAdmin) return Forbid();

            var ticket = await _db.SupportTickets.FindAsync(id);
            if (ticket == null) return NotFound();

            ticket.IsClosed = true;
            ticket.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();

            await _hub.Clients.Group($"user-{ticket.UserId}")
                .SendAsync("TicketClosed", ticket.Id);
            await _hub.Clients.Group("support-admins")
                .SendAsync("TicketClosed", ticket.Id);

            return Ok();
        }

        public class CreateRequest
        {
            public string? Type { get; set; }
            public string Message { get; set; } = string.Empty;
        }

        public class AddMessageRequest
        {
            public string Message { get; set; } = string.Empty;
        }
    }
}
