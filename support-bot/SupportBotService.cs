using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using taskforge.Data;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace SupportBot
{
    /// <summary>
    /// HostedService, работающий с Telegram API. Отправляет сообщения пользователей
    /// в группу поддержки и обрабатывает ответы админов как сообщения в тикете.
    ///
    /// ВАЖНО: бот НЕ сохраняет ответы админов в БД. Он только форвардит их в API,
    /// иначе будет дубль (бот сохраняет + API сохраняет).
    /// </summary>
    public class SupportBotService : BackgroundService
    {
        private readonly IServiceProvider _provider;
        private readonly ILogger<SupportBotService> _logger;
        private readonly IConfiguration _configuration;
        private TelegramBotClient? _bot;
        private long _groupId;
        private string? _apiBaseUrl;
        private string? _apiKey;

        public SupportBotService(IServiceProvider provider, ILogger<SupportBotService> logger, IConfiguration configuration)
        {
            _provider = provider;
            _logger = logger;
            _configuration = configuration;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var token = _configuration["TELEGRAM_BOT_TOKEN"];
            var groupIdStr = _configuration["TELEGRAM_SUPPORT_GROUP_ID"];
            _apiBaseUrl = _configuration["API_BASE_URL"];
            _apiKey = _configuration["API_INTERNAL_KEY"];

            if (string.IsNullOrWhiteSpace(token) ||
                string.IsNullOrWhiteSpace(groupIdStr) ||
                string.IsNullOrWhiteSpace(_apiBaseUrl) ||
                string.IsNullOrWhiteSpace(_apiKey))
            {
                _logger.LogError("SupportBotService: environment variables are not set");
                return;
            }

            if (!long.TryParse(groupIdStr, out _groupId))
            {
                _logger.LogError("SupportBotService: TELEGRAM_SUPPORT_GROUP_ID is invalid");
                return;
            }

            _bot = new TelegramBotClient(token);

            try
            {
                // Удаляем вебхук на случай, если ранее был установлен, чтобы переходить в режим polling
                await _bot.DeleteWebhookAsync(cancellationToken: stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete webhook");
            }

            var receiverOptions = new ReceiverOptions
            {
                AllowedUpdates = Array.Empty<UpdateType>()
            };

            _bot.StartReceiving(HandleUpdateAsync, HandleErrorAsync, receiverOptions, cancellationToken: stoppingToken);
            _logger.LogInformation("Support bot started receiving updates");

            // Главный цикл: отправка неотправленных сообщений в Telegram
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await SendUnsentMessagesAsync(stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error while sending unsent messages");
                }

                // Опрашиваем каждые 10 секунд
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }

        /// <summary>
        /// Отправляет все сообщения пользователей, которые ещё не отправлены в Telegram (TelegramMessageId == null).
        /// </summary>
        private async Task SendUnsentMessagesAsync(CancellationToken ct)
        {
            if (_bot == null) return;

            using var scope = _provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var unsent = await db.SupportMessages
                .Include(m => m.Ticket)
                .ThenInclude(t => t.User)
                .Where(m => !m.IsFromAdmin && m.TelegramMessageId == null)
                .ToListAsync(ct);

            foreach (var msg in unsent)
            {
                var user = msg.Ticket.User;
                var header =
                    $"🛠️ Ticket {msg.Ticket.Id}\n" +
                    $"Тип: {msg.Ticket.Type}\n" +
                    $"Пользователь: {user.FirstName} {user.LastName}\n" +
                    $"Email: {user.Email}\n\n";

                var sent = await _bot.SendTextMessageAsync(_groupId, header + msg.Text, cancellationToken: ct);

                msg.TelegramChatId = _groupId;
                msg.TelegramMessageId = sent.MessageId;

                await db.SaveChangesAsync(ct);
                _logger.LogInformation($"Sent support message {msg.Id} to group {_groupId}");
            }
        }

        /// <summary>
        /// Обработчик обновлений Telegram. Берём только ответы (reply) в группе.
        /// Бот НЕ пишет ответ в БД, а только отправляет событие в API.
        /// </summary>
        private async Task HandleUpdateAsync(ITelegramBotClient bot, Update update, CancellationToken ct)
        {
            try
            {
                if (update.Message == null) return;

                // ===== Привязка Telegram (личные сообщения боту) =====
                if (update.Message.Chat.Type == ChatType.Private)
                {
                    await HandlePrivateMessageAsync(update.Message, ct);
                    return;
                }

                if (update.Message.ReplyToMessage == null) return;
                if (string.IsNullOrWhiteSpace(update.Message.Text)) return;

                var replyId = update.Message.ReplyToMessage.MessageId;

                using var scope = _provider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

                // Находим исходное сообщение пользователя, которое бот ранее отправлял в TG
                var original = await db.SupportMessages
                    .Include(m => m.Ticket)
                    .FirstOrDefaultAsync(m => m.TelegramMessageId == replyId, ct);

                if (original == null) return;

                var ticket = original.Ticket;

                var firstName = update.Message.From?.FirstName ?? string.Empty;
                var lastName = update.Message.From?.LastName ?? string.Empty;
                var authorName = $"{firstName} {lastName}".Trim();
                if (string.IsNullOrWhiteSpace(authorName))
                    authorName = update.Message.From?.Username ?? "Admin";

                var text = update.Message.Text ?? string.Empty;

                _logger.LogInformation($"Received reply for ticket {ticket.Id} (TG msg {update.Message.MessageId})");

                // ВАЖНО: сохраняет и рассылает только API, иначе будет дубль.
                await NotifyApiAsync(ticket.Id, authorName, text, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in HandleUpdateAsync");
            }
        }

        private async Task HandlePrivateMessageAsync(Message msg, CancellationToken ct)
        {
            if (_bot == null) return;
            if (string.IsNullOrWhiteSpace(msg.Text)) return;

            var text = (msg.Text ?? string.Empty).Trim();
            if (text.StartsWith("/start", StringComparison.OrdinalIgnoreCase))
            {
                await _bot.SendTextMessageAsync(msg.Chat.Id,
                    "Привет! Чтобы привязать Telegram к TaskForge:\n\n1) Открой TaskForge → Профиль → Telegram\n2) Сгенерируй код\n3) Отправь мне этот код сюда (в личку).\n\nЯ отвечу, получилось ли привязать.",
                    cancellationToken: ct);
                return;
            }

            // Берём первый токен как код, поддерживаем формат XXXX-XXXX
            var code = text.Split(new[] { ' ', '\n', '\t', '\r' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (string.IsNullOrWhiteSpace(code)) return;

            // Быстрая валидация (чтобы не спамить API)
            var normalized = code.Trim().ToUpperInvariant();
            if (normalized.Length < 4 || normalized.Length > 32)
            {
                await _bot.SendTextMessageAsync(msg.Chat.Id,
                    "Похоже, это не код. Сгенерируй код на сайте TaskForge и отправь его мне сюда.",
                    cancellationToken: ct);
                return;
            }

            var username = msg.From?.Username;
            var payload = new { code = normalized, chatId = msg.Chat.Id, username };

            var ok = await CallTelegramConfirmAsync(payload, ct);
            await _bot.SendTextMessageAsync(msg.Chat.Id, ok.message, cancellationToken: ct);
        }

        private async Task<(bool success, string message)> CallTelegramConfirmAsync(object payload, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(_apiBaseUrl) || string.IsNullOrEmpty(_apiKey))
                return (false, "Сервис сейчас недоступен. Попробуй позже.");

            using var scope = _provider.CreateScope();
            var clientFactory = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>();
            var http = clientFactory.CreateClient();

            var request = new HttpRequestMessage(HttpMethod.Post, $"{_apiBaseUrl}/api/integrations/telegram/confirm")
            {
                Content = JsonContent.Create(payload)
            };
            request.Headers.Add("X-Internal-Key", _apiKey);

            try
            {
                var resp = await http.SendAsync(request, ct);
                var body = await resp.Content.ReadAsStringAsync(ct);

                if (resp.IsSuccessStatusCode)
                {
                    // API возвращает { ok, message }
                    try
                    {
                        var dto = System.Text.Json.JsonSerializer.Deserialize<ConfirmResp>(body,
                            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                        if (dto != null && dto.Ok)
                            return (true, "✅ " + (dto.Message ?? "Telegram привязан. Можно вернуться на сайт."));
                        return (false, dto?.Message ?? "Не удалось привязать Telegram.");
                    }
                    catch
                    {
                        return (true, "✅ Telegram привязан. Можно вернуться на сайт.");
                    }
                }

                // Пытаемся вытащить message из ошибки
                try
                {
                    var dto = System.Text.Json.JsonSerializer.Deserialize<ConfirmResp>(body,
                        new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (dto != null && !string.IsNullOrWhiteSpace(dto.Message))
                        return (false, "❌ " + dto.Message);
                }
                catch { }

                return (false, "❌ Не удалось привязать. Проверь код и попробуй ещё раз. (" + (int)resp.StatusCode + ")");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error calling telegram confirm API");
                return (false, "Сервис сейчас недоступен. Попробуй позже.");
            }
        }

        private sealed class ConfirmResp
        {
            public bool Ok { get; set; }
            public string? Message { get; set; }
        }

        private Task HandleErrorAsync(ITelegramBotClient bot, Exception exception, CancellationToken ct)
        {
            var errorMessage = exception switch
            {
                ApiRequestException apiRequestException
                    => $"Telegram API Error:\n[{apiRequestException.ErrorCode}] {apiRequestException.Message}",
                _ => exception.ToString()
            };
            _logger.LogError(errorMessage);
            return Task.CompletedTask;
        }

        /// <summary>
        /// Уведомляет API о новом ответе администратора по тикету. Отправляет POST на /api/support/events/new.
        /// </summary>
        private async Task NotifyApiAsync(Guid ticketId, string authorName, string message, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(_apiBaseUrl) || string.IsNullOrEmpty(_apiKey)) return;

            using var scope = _provider.CreateScope();
            var clientFactory = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>();
            var http = clientFactory.CreateClient();

            var payload = new { ticketId, authorName, message };
            var request = new HttpRequestMessage(HttpMethod.Post, $"{_apiBaseUrl}/api/support/events/new")
            {
                Content = JsonContent.Create(payload)
            };
            request.Headers.Add("X-Internal-Key", _apiKey);

            try
            {
                var resp = await http.SendAsync(request, ct);
                if (!resp.IsSuccessStatusCode)
                {
                    var body = await resp.Content.ReadAsStringAsync(ct);
                    _logger.LogWarning($"Failed to notify API. Status: {resp.StatusCode}. Body: {body}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending notification to API");
            }
        }
    }
}
