using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using taskforge.Data;
using taskforge.Data.Models.DTO.Support;
using taskforge.Data.Models.Entities;
using taskforge.Hubs;
using taskforge.Services.Interfaces;

namespace taskforge.Services.Support
{
    /// <summary>
    /// Сервис поддержки: работа с тикетами/сообщениями и рассылка событий через SignalR.
    /// Держит контроллеры тонкими и убирает дублирование логики.
    /// </summary>
    public sealed class SupportService : ISupportService
    {
        private readonly ApplicationDbContext _db;
        private readonly IHubContext<SupportHub> _hub;

        public SupportService(ApplicationDbContext db, IHubContext<SupportHub> hub)
        {
            _db = db;
            _hub = hub;
        }

        public async Task<IReadOnlyList<SupportTicketListItemDto>> GetTicketsAsync(Guid userId, bool isSupportAdmin, CancellationToken ct)
        {
            var query = _db.SupportTickets
                .Include(t => t.Messages)
                .AsQueryable();

            if (!isSupportAdmin)
            {
                query = query.Where(t => t.UserId == userId);
            }

            var tickets = await query
                .OrderByDescending(t => t.UpdatedAt)
                .Select(t => new SupportTicketListItemDto
                {
                    Id = t.Id,
                    Type = t.Type,
                    IsClosed = t.IsClosed,
                    CreatedAt = t.CreatedAt,
                    UpdatedAt = t.UpdatedAt,
                    MessagesCount = t.Messages.Count
                })
                .ToListAsync(ct);

            return tickets;
        }

        public async Task<SupportTicketDetailsDto?> GetTicketAsync(Guid ticketId, Guid userId, bool isSupportAdmin, CancellationToken ct)
        {
            var ticket = await _db.SupportTickets
                .Include(t => t.Messages)
                .Include(t => t.User)
                .FirstOrDefaultAsync(t => t.Id == ticketId, ct);

            if (ticket == null) return null;
            if (!isSupportAdmin && ticket.UserId != userId)
                throw new UnauthorizedAccessException("Forbidden");

            var userDisplayName = $"{ticket.User.FirstName} {ticket.User.LastName}".Trim();

            return new SupportTicketDetailsDto
            {
                Id = ticket.Id,
                Type = ticket.Type,
                CreatedAt = ticket.CreatedAt,
                UpdatedAt = ticket.UpdatedAt,
                IsClosed = ticket.IsClosed,
                Messages = ticket.Messages
                    .OrderBy(m => m.CreatedAt)
                    .Select(m => new SupportMessageDto
                    {
                        Id = m.Id,
                        Text = m.Text,
                        CreatedAt = m.CreatedAt,
                        IsFromAdmin = m.IsFromAdmin,
                        AuthorName = m.IsFromAdmin
                            ? (m.AuthorName ?? "Админ")
                            : userDisplayName
                    })
                    .ToList()
            };
        }

        public async Task<Guid> CreateTicketAsync(Guid userId, string? type, string message, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(message))
                throw new ValidationException("Сообщение не может быть пустым");

            var ticketType = string.IsNullOrWhiteSpace(type) ? "question" : type.Trim();

            var ticket = new SupportTicket
            {
                UserId = userId,
                Type = ticketType,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                IsClosed = false
            };

            var msg = new SupportMessage
            {
                Ticket = ticket,
                AuthorUserId = userId,
                Text = message.Trim(),
                CreatedAt = DateTime.UtcNow,
                IsFromAdmin = false,
                Source = "SiteUser"
            };

            _db.SupportTickets.Add(ticket);
            _db.SupportMessages.Add(msg);
            await _db.SaveChangesAsync(ct);

            var author = await _db.Users.FindAsync(new object?[] { userId }, ct);
            var authorName = author != null
                ? $"{author.FirstName} {author.LastName}".Trim()
                : string.Empty;

            var payload = new
            {
                msg.Id,
                msg.Text,
                msg.CreatedAt,
                msg.IsFromAdmin,
                AuthorName = authorName
            };

            await _hub.Clients.Group($"user-{userId}")
                .SendAsync("ReceiveMessage", ticket.Id, payload, ct);
            await _hub.Clients.Group("support-admins")
                .SendAsync("ReceiveMessage", ticket.Id, payload, ct);

            return ticket.Id;
        }

        public async Task<Guid> AddMessageAsync(Guid ticketId, Guid userId, bool isSupportAdmin, string? adminName, string message, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(message))
                throw new ValidationException("Сообщение не может быть пустым");

            var ticket = await _db.SupportTickets
                .Include(t => t.User)
                .FirstOrDefaultAsync(t => t.Id == ticketId, ct);
            if (ticket == null) throw new KeyNotFoundException("Ticket not found");

            if (!isSupportAdmin && ticket.UserId != userId)
                throw new UnauthorizedAccessException("Forbidden");

            var msg = new SupportMessage
            {
                TicketId = ticket.Id,
                AuthorUserId = isSupportAdmin ? (Guid?)null : userId,
                AuthorName = isSupportAdmin ? adminName : null,
                Text = message.Trim(),
                CreatedAt = DateTime.UtcNow,
                IsFromAdmin = isSupportAdmin,
                Source = isSupportAdmin ? "SiteAdmin" : "SiteUser"
            };

            _db.SupportMessages.Add(msg);
            ticket.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);

            var payload = new
            {
                msg.Id,
                msg.Text,
                msg.CreatedAt,
                msg.IsFromAdmin,
                AuthorName = msg.IsFromAdmin
                    ? (msg.AuthorName ?? "Админ")
                    : $"{ticket.User.FirstName} {ticket.User.LastName}".Trim()
            };

            await _hub.Clients.Group($"user-{ticket.UserId}")
                .SendAsync("ReceiveMessage", ticket.Id, payload, ct);
            await _hub.Clients.Group("support-admins")
                .SendAsync("ReceiveMessage", ticket.Id, payload, ct);
            await _hub.Clients.Group($"ticket-{ticket.Id}")
                .SendAsync("ReceiveMessage", ticket.Id, payload, ct);

            return msg.Id;
        }

        public async Task CloseTicketAsync(Guid ticketId, bool isSupportAdmin, CancellationToken ct)
        {
            if (!isSupportAdmin)
                throw new UnauthorizedAccessException("Forbidden");

            var ticket = await _db.SupportTickets.FindAsync(new object?[] { ticketId }, ct);
            if (ticket == null) throw new KeyNotFoundException("Ticket not found");

            ticket.IsClosed = true;
            ticket.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);

            await _hub.Clients.Group($"user-{ticket.UserId}")
                .SendAsync("TicketClosed", ticket.Id, ct);
            await _hub.Clients.Group("support-admins")
                .SendAsync("TicketClosed", ticket.Id, ct);
        }

        public async Task AddExternalAdminMessageAsync(Guid ticketId, string authorName, string message, string source, string? externalMessageId, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(message))
                throw new ValidationException("Сообщение не может быть пустым");

            var ticket = await _db.SupportTickets.FirstOrDefaultAsync(t => t.Id == ticketId, ct);
            if (ticket == null) throw new KeyNotFoundException("Ticket not found");

            // Простая дедупликация (на случай если одну и ту же реплику сохраняют два источника).
            // Не требует изменения схемы БД.
            var trimmed = message.Trim();
            var dedupSince = DateTime.UtcNow.AddSeconds(-15);
            var exists = await _db.SupportMessages.AnyAsync(m =>
                m.TicketId == ticketId
                && m.IsFromAdmin
                && m.Source == source
                && m.Text == trimmed
                && m.CreatedAt >= dedupSince,
                ct);
            if (exists) return;

            var msg = new SupportMessage
            {
                TicketId = ticketId,
                Text = trimmed,
                CreatedAt = DateTime.UtcNow,
                IsFromAdmin = true,
                AuthorName = authorName,
                Source = source
            };

            _db.SupportMessages.Add(msg);
            ticket.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);

            var payload = new
            {
                msg.Id,
                msg.Text,
                msg.CreatedAt,
                msg.IsFromAdmin,
                AuthorName = msg.AuthorName ?? "Админ"
            };

            await _hub.Clients.Group($"user-{ticket.UserId}")
                .SendAsync("ReceiveMessage", ticket.Id, payload, ct);
            await _hub.Clients.Group("support-admins")
                .SendAsync("ReceiveMessage", ticket.Id, payload, ct);
            await _hub.Clients.Group($"ticket-{ticket.Id}")
                .SendAsync("ReceiveMessage", ticket.Id, payload, ct);
        }
    }
}
