using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using taskforge.Data;
using taskforge.Data.Models.DTO;

namespace taskforge.Controllers
{
    [ApiController]
    [Route("api")]
    public sealed class SupportController : ControllerBase
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IConfiguration _config;
        private readonly ILogger<SupportController> _logger;
        private readonly ApplicationDbContext _db;

        public SupportController(
            IHttpClientFactory httpClientFactory,
            IConfiguration config,
            ILogger<SupportController> logger,
            ApplicationDbContext db)
        {
            _httpClientFactory = httpClientFactory;
            _config = config;
            _logger = logger;
            _db = db;
        }

        [Authorize]
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

            // Инициализировать токен и chatId
            var token = "8548368756:AAGoxV2eda_gptaD7IPXyzqb3jDR-mQv-JM";
            var chatId = "1202503239";
            if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(chatId))
                return StatusCode(500, new { message = "Служба поддержки не настроена (отсутствует BotToken или ChatId)." });

            // Пытаемся получить данные пользователя
            string firstName = "";
            string lastName = "";
            string email = "";
            string phone = "";
            try
            {
                var userIdString = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
                if (userIdString != null && Guid.TryParse(userIdString, out var userId))
                {
                    var user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct);
                    if (user != null)
                    {
                        firstName = user.FirstName;
                        lastName = user.LastName;
                        email = user.Email;
                        phone = user.PhoneNumber ?? "";
                    }
                }
            }
            catch { /* если что‑то пошло не так, просто оставим поля пустыми */ }

            var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "-";
            var ua = Request.Headers.UserAgent.ToString();

            // Формируем текст сообщения (без отображения ID)
            var text =
                "🛠️ <b>Обращение в поддержку</b>\n" +
                $"Тип: {type}\n" +
                (!string.IsNullOrWhiteSpace(firstName) || !string.IsNullOrWhiteSpace(lastName) ? $"Имя: {firstName} {lastName}\n" : "") +
                (!string.IsNullOrWhiteSpace(email) ? $"Email: {email}\n" : "") +
                (!string.IsNullOrWhiteSpace(phone) ? $"Телефон: {phone}\n" : "") +
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
