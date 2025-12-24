using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using taskforge.Data;
using taskforge.Data.Models.Entities;

namespace taskforge.Controllers
{
    [ApiController]
    [Route("api/telegram")]
    public class TelegramController : ControllerBase
    {
        private readonly ApplicationDbContext _db;
        private readonly ILogger<TelegramController> _logger;

        public TelegramController(ApplicationDbContext db, ILogger<TelegramController> logger)
        {
            _db = db;
            _logger = logger;
        }

        [HttpPost("webhook")]
        [AllowAnonymous]
        public async Task<IActionResult> Webhook([FromBody] TelegramUpdate update, CancellationToken ct)
        {
            // Интересуют только ответы (reply) на существующие сообщения
            var message = update?.message;
            if (message?.reply_to_message?.message_id == null || string.IsNullOrWhiteSpace(message.text))
                return Ok();

            var repliedId = message.reply_to_message.message_id;

            // Находим исходное SupportMessage
            var originalMsg = await _db.SupportMessages
                .Include(m => m.Ticket)
                .FirstOrDefaultAsync(m => m.TelegramMessageId == repliedId, ct);

            if (originalMsg == null)
            {
                _logger.LogWarning("Received reply to unknown telegram message ID {Id}", repliedId);
                return Ok();
            }

            var ticket = originalMsg.Ticket;
            if (ticket == null)
            {
                _logger.LogWarning("Original message has no associated ticket.");
                return Ok();
            }

            // Формируем новое сообщение от админа
            var adminMsg = new SupportMessage
            {
                TicketId = ticket.Id,
                AuthorId = null, // нет привязки к пользователю в нашей системе
                AuthorName = $"{update.message.from.first_name} {update.message.from.last_name}".Trim(),
                Text = message.text,
                CreatedAt = DateTime.UtcNow,
                IsFromAdmin = true
            };

            _db.SupportMessages.Add(adminMsg);
            ticket.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);

            return Ok();
        }

        // Примитивные классы для Telegram JSON (минимум полей)
        public class TelegramUpdate
        {
            public TelegramMessage? message { get; set; }
        }

        public class TelegramMessage
        {
            public long message_id { get; set; }
            public TelegramUser? from { get; set; }
            public TelegramMessage? reply_to_message { get; set; }
            public string? text { get; set; }
        }

        public class TelegramUser
        {
            public string? first_name { get; set; }
            public string? last_name { get; set; }
            public string? username { get; set; }
        }
    }
}
