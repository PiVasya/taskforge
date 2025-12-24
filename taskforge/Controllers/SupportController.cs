using System;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using taskforge.Data;
using taskforge.Data.Models.DTO;
using taskforge.Data.Models.Entities;

namespace taskforge.Controllers
{
    [ApiController]
    [Route("api/support")]
    [Authorize]
    public class SupportController : ControllerBase
    {
        private readonly ApplicationDbContext _db;
        private readonly IHttpClientFactory _httpFactory;
        private readonly IConfiguration _cfg;
        private readonly ILogger<SupportController> _logger;

        public SupportController(ApplicationDbContext db, IHttpClientFactory httpFactory, IConfiguration cfg, ILogger<SupportController> logger)
        {
            _db = db;
            _httpFactory = httpFactory;
            _cfg = cfg;
            _logger = logger;
        }

        /// <summary>
        /// Создать новое обращение. Возвращает ticketId.
        /// </summary>
        [HttpPost]
        public async Task<IActionResult> Create([FromBody] SupportMessageDto dto, CancellationToken ct)
        {
            var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

            // валидация
            var msg = (dto.Message ?? "").Trim();
            if (string.IsNullOrWhiteSpace(msg) || msg.Length > 2000)
                return BadRequest(new { message = "Сообщение не может быть пустым и не может быть длиннее 2000 символов." });

            var type = (dto.Type ?? "question").Trim();
            if (string.IsNullOrWhiteSpace(type)) type = "question";

            // создаём тикет и первое сообщение
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
                AuthorId = userId,
                Text = msg,
                CreatedAt = DateTime.UtcNow,
                IsFromAdmin = false
            };

            _db.SupportTickets.Add(ticket);
            _db.SupportMessages.Add(message);
            await _db.SaveChangesAsync(ct);

            // Отправляем уведомление в Telegram
            var telegramId = await SendToTelegramAsync(ticket, message, ct);
            message.TelegramMessageId = telegramId;
            await _db.SaveChangesAsync(ct);

            return Ok(new { ticketId = ticket.Id });
        }

        /// <summary>
        /// Вернуть список обращений текущего пользователя.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> List(CancellationToken ct)
        {
            var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
            var tickets = await _db.SupportTickets
                .Where(t => t.UserId == userId)
                .OrderByDescending(t => t.UpdatedAt)
                .Select(t => new
                {
                    t.Id,
                    t.Type,
                    t.CreatedAt,
                    t.UpdatedAt,
                    t.IsClosed,
                    MessagesCount = t.Messages.Count
                })
                .ToListAsync(ct);

            return Ok(tickets);
        }

        /// <summary>
        /// Вернуть подробности обращения и все сообщения.
        /// </summary>
        [HttpGet("{ticketId:guid}")]
        public async Task<IActionResult> Get(Guid ticketId, CancellationToken ct)
        {
            var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
            var ticket = await _db.SupportTickets
                .Include(t => t.Messages.OrderBy(m => m.CreatedAt))
                .FirstOrDefaultAsync(t => t.Id == ticketId, ct);

            if (ticket == null || ticket.UserId != userId)
                return NotFound();

            // Получаем имя/фамилию пользователя для подписи
            var user = await _db.Users.FirstAsync(u => u.Id == userId, ct);

            var messages = ticket.Messages.OrderBy(m => m.CreatedAt).Select(m => new
            {
                m.Id,
                m.Text,
                m.CreatedAt,
                m.IsFromAdmin,
                AuthorName = m.IsFromAdmin
                    ? (string.IsNullOrEmpty(m.AuthorName) ? "Администратор" : m.AuthorName)
                    : $"{user.FirstName} {user.LastName}"
            });

            return Ok(new
            {
                ticket = new { ticket.Id, ticket.Type, ticket.CreatedAt, ticket.UpdatedAt, ticket.IsClosed },
                messages
            });
        }

        /// <summary>
        /// Добавить новое сообщение в уже существующее обращение (ответ пользователя).
        /// </summary>
        [HttpPost("{ticketId:guid}")]
        public async Task<IActionResult> Reply(Guid ticketId, [FromBody] SupportMessageDto dto, CancellationToken ct)
        {
            var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
            var ticket = await _db.SupportTickets.FirstOrDefaultAsync(t => t.Id == ticketId, ct);

            if (ticket == null || ticket.UserId != userId)
                return NotFound();

            var msg = (dto.Message ?? "").Trim();
            if (string.IsNullOrWhiteSpace(msg) || msg.Length > 2000)
                return BadRequest(new { message = "Сообщение не может быть пустым и не может быть длиннее 2000 символов." });

            var message = new SupportMessage
            {
                TicketId = ticketId,
                AuthorId = userId,
                Text = msg,
                CreatedAt = DateTime.UtcNow,
                IsFromAdmin = false
            };

            _db.SupportMessages.Add(message);
            ticket.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);

            // Отправляем новое сообщение в Telegram
            var telegramId = await SendToTelegramAsync(ticket, message, ct);
            message.TelegramMessageId = telegramId;
            await _db.SaveChangesAsync(ct);

            return Ok(new { id = message.Id });
        }

        /// <summary>
        /// Внутренний метод: отправляет текст в Telegram и возвращает message_id.
        /// </summary>
        private async Task<long?> SendToTelegramAsync(SupportTicket ticket, SupportMessage message, CancellationToken ct)
        {
            var user = await _db.Users.FirstAsync(u => u.Id == ticket.UserId, ct);
            var text =
                "🛠️ <b>Новое сообщение в обращении</b>\n" +
                $"Ticket: {ticket.Id}\n" +
                $"Тип: {ticket.Type}\n" +
                $"Пользователь: {user.FirstName} {user.LastName}\n" +
                $"Email: {user.Email}\n" +
                "--------------------\n" +
                message.Text;

            var token = _cfg["Telegram:BotToken"] ?? _cfg["TELEGRAM_BOT_TOKEN"];
            var chatId = _cfg["Telegram:SupportChatId"] ?? _cfg["SUPPORT_CHAT_ID"];

            if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(chatId))
            {
                _logger.LogError("Telegram is not configured.");
                return null;
            }

            try
            {
                var client = _httpFactory.CreateClient();
                var response = await client.PostAsJsonAsync($"https://api.telegram.org/bot{token}/sendMessage", new
                {
                    chat_id = chatId,
                    text,
                    parse_mode = "HTML"
                }, ct);

                if (!response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync(ct);
                    _logger.LogError("Telegram sendMessage failed: {Status} {Body}", response.StatusCode, body);
                    return null;
                }

                var json = await response.Content.ReadFromJsonAsync<dynamic>(cancellationToken: ct);
                return (long?)json?.result?.message_id;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send to Telegram");
                return null;
            }
        }
    }
}
