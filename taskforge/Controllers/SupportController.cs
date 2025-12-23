using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using taskforge.Data.Models.DTO;

namespace taskforge.Controllers
{
    /// <summary>
    /// Принимает обращения в поддержку и отправляет их владельцу через Telegram‑бота.
    /// </summary>
    [ApiController]
    [Route("api")]
    public sealed class SupportController : ControllerBase
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IConfiguration _config;
        private readonly ILogger<SupportController> _logger;

        public SupportController(
            IHttpClientFactory httpClientFactory,
            IConfiguration config,
            ILogger<SupportController> logger)
        {
            _httpClientFactory = httpClientFactory;
            _config = config;
            _logger = logger;
        }

        /// <summary>
        /// Отправить обращение в поддержку.
        /// </summary>
        /// <param name="request">Тип вопроса и текст сообщения.</param>
        [Authorize] // при желании можно поменять на [AllowAnonymous], если поддержка доступна без входа
        [HttpPost("support")]
        public async Task<IActionResult> Send([FromBody] SupportMessageDto request, CancellationToken ct)
        {
            if (request == null)
                return BadRequest(new { message = "Пустой запрос." });

            var type = (request.Type ?? "").Trim();
            var msg = (request.Message ?? "").Trim();

            if (string.IsNullOrWhiteSpace(msg))
                return BadRequest(new { message = "Сообщение не может быть пустым." });
            if (msg.Length > 2000)
                return BadRequest(new { message = "Сообщение слишком длинное (максимум 2000 символов)." });

            // Пример: читаем токен/чат из appsettings.json или переменных окружения
            var token = "8548368756:AAGoxV2eda_gptaD7IPXyzqb3jDR-mQv-JM";
            var chatId = "1202503239";

            if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(chatId))
            {
                return StatusCode(500, new { message = "Служба поддержки не настроена (отсутствует BotToken или ChatId)." });
            }

            // Достаём данные о пользователе (если авторизован)
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)
                       ?? User.FindFirstValue("sub")
                       ?? "-";
            var userEmail = User.FindFirstValue(ClaimTypes.Email)
                         ?? User.FindFirstValue("email")
                         ?? "";
            var userName = User.Identity?.Name
                       ?? User.FindFirstValue("name")
                       ?? "";

            var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "-";
            var ua = Request.Headers.UserAgent.ToString();

            // Формируем текст для Telegram
            var text =
                "🛠️ <b>Обращение в поддержку</b>\n" +
                $"Тип: {type}\n" +
                $"UserId: {userId}\n" +
                (!string.IsNullOrWhiteSpace(userName) ? $"Имя: {userName}\n" : "") +
                (!string.IsNullOrWhiteSpace(userEmail) ? $"Email: {userEmail}\n" : "") +
                $"IP: {ip}\n" +
                (!string.IsNullOrWhiteSpace(ua) ? $"UA: {ua}\n" : "") +
                "--------------------\n" +
                msg;

            var url = $"https://api.telegram.org/bot{token}/sendMessage";

            try
            {
                var client = _httpClientFactory.CreateClient();
                var response = await client.PostAsJsonAsync(url, new
                {
                    chat_id = chatId,
                    text = text,
                    parse_mode = "HTML"
                }, ct);

                if (!response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync(ct);
                    _logger.LogError("Telegram sendMessage failed. Status={Status} Body={Body}",
                        response.StatusCode, body);
                    return StatusCode(502, new { message = "Не удалось отправить сообщение в Telegram." });
                }

                return Ok(new { ok = true });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Support send failed");
                return StatusCode(500, new { message = "Ошибка отправки сообщения." });
            }
        }
    }
}
