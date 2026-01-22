using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using taskforge.Data;
using taskforge.Data.Models.Entities;
using taskforge.Services.Interfaces;

namespace taskforge.Controllers
{
    /// <summary>
    /// Контроллер для обработки вебхуков Telegram. Используется для
    /// преобразования ответов администраторов в новые сообщения тикета.
    /// </summary>
    [ApiController]
    [Route("api/telegram")]
    public class TelegramController : ControllerBase
    {
        private readonly ApplicationDbContext _db;
        private readonly ILogger<TelegramController> _logger;
        private readonly ISupportService _support;

        public TelegramController(ApplicationDbContext db, ILogger<TelegramController> logger, ISupportService support)
        {
            _db = db;
            _logger = logger;
            _support = support;
        }

        /// <summary>
        /// Вебхук Telegram. Получает обновления и сохраняет ответы администраторов.
        /// Только ответы (reply) на существующие сообщения тикетов обрабатываются.
        /// </summary>
        [HttpPost("webhook")]
        [AllowAnonymous]
        public async Task<IActionResult> Webhook([FromBody] TelegramUpdate update, CancellationToken ct)
        {
            var message = update?.message;
            // Обрабатываем только ответы на известные сообщения с текстом
            if (message?.reply_to_message?.message_id == null || string.IsNullOrWhiteSpace(message.text))
                return Ok();

            var repliedId = message.reply_to_message.message_id;
            // Находим исходное сообщение, чтобы понять тикет
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

            var authorName = $"{message.@from?.first_name} {message.@from?.last_name}".Trim();
            await _support.AddExternalAdminMessageAsync(
                ticket.Id,
                authorName,
                message.text ?? string.Empty,
                source: "TelegramAdmin",
                externalMessageId: message.message_id.ToString(),
                ct: ct);
            return Ok();
        }

        // Ниже определены минимальные модели для десериализации обновления Telegram. Telegram
        // использует нестандартные имена полей (from), поэтому имена свойств помечены
        // @ для предотвращения конфликтов с ключевыми словами C#.
        public class TelegramUpdate
        {
            public TelegramMessage? message { get; set; }
        }

        public class TelegramMessage
        {
            public long message_id { get; set; }
            public TelegramUser? @from { get; set; }
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