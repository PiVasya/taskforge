using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Authorization;
using taskforge.Data.Models.DTO.Support;
using taskforge.Services.Interfaces;

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
        private readonly IConfiguration _config;
        private readonly ISupportService _support;

        public SupportEventsController(IConfiguration config, ISupportService support)
        {
            _config = config;
            _support = support;
        }

        /// <summary>
        /// Обработать новое сообщение от админа из внешнего канала (например, Telegram).
        /// Проверяет internal key в заголовке X-Internal-Key.
        /// </summary>
        [HttpPost("new")]
        [AllowAnonymous]
        public async Task<IActionResult> New([FromBody] SupportNewEventRequestDto request)
        {
            var expectedKey = _config["API_INTERNAL_KEY"];
            var header = Request.Headers["X-Internal-Key"].ToString();
            if (string.IsNullOrEmpty(expectedKey) || header != expectedKey)
            {
                return Unauthorized();
            }
            await _support.AddExternalAdminMessageAsync(
                request.TicketId,
                request.AuthorName,
                request.Message,
                source: "TelegramAdmin",
                externalMessageId: request.ExternalMessageId,
                ct: HttpContext.RequestAborted);

            return Ok();
        }
    }
}