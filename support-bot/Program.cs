using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using taskforge.Data;
using taskforge.Data.Models.Entities;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;

namespace SupportBot
{
    /// <summary>
    /// Точка входа сервиса техподдержки.
    /// </summary>
    public static class Program
    {
        public static async Task Main(string[] args)
        {
            var host = Host.CreateDefaultBuilder(args)
                .ConfigureServices((context, services) =>
                {
                    var configuration = context.Configuration;
                    services.AddDbContext<ApplicationDbContext>(options =>
                        options.UseNpgsql(configuration.GetConnectionString("DefaultConnection")));
                    services.AddHttpClient();
                    services.AddHostedService<SupportBotService>();
                })
                .Build();

            await host.RunAsync();
        }
    }

    /// <summary>
    /// HostedService, работающий с Telegram API.
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
            if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(groupIdStr) || string.IsNullOrWhiteSpace(_apiBaseUrl) || string.IsNullOrWhiteSpace(_apiKey))
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
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }

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
                var header = $"🛠️ Ticket {msg.Ticket.Id}\nТип: {msg.Ticket.Type}\nПользователь: {user.FirstName} {user.LastName}\nEmail: {user.Email}\n\n";
                var sent = await _bot.SendTextMessageAsync(_groupId, header + msg.Text, cancellationToken: ct);
                msg.TelegramChatId = _groupId;
                msg.TelegramMessageId = sent.MessageId;
                await db.SaveChangesAsync(ct);
                _logger.LogInformation($"Sent support message {msg.Id} to group {_groupId}");
            }
        }

        private async Task HandleUpdateAsync(ITelegramBotClient bot, Update update, CancellationToken ct)
        {
            try
            {
                if (update.Message != null && update.Message.ReplyToMessage != null && !string.IsNullOrWhiteSpace(update.Message.Text))
                {
                    var replyId = update.Message.ReplyToMessage.MessageId;
                    using var scope = _provider.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                    var original = await db.SupportMessages
                        .Include(m => m.Ticket)
                        .FirstOrDefaultAsync(m => m.TelegramMessageId == replyId, ct);
                    if (original == null)
                    {
                        return;
                    }
                    var ticket = original.Ticket;
                    var authorName = $"{update.Message.From?.FirstName} {update.Message.From?.LastName}".Trim();
                    var newMsg = new SupportMessage
                    {
                        TicketId = ticket.Id,
                        Text = update.Message.Text ?? string.Empty,
                        CreatedAt = DateTime.UtcNow,
                        IsFromAdmin = true,
                        AuthorName = string.IsNullOrWhiteSpace(authorName) ? update.Message.From?.Username : authorName,
                        TelegramChatId = update.Message.Chat.Id,
                        TelegramMessageId = update.Message.MessageId,
                        Source = "TelegramAdmin"
                    };
                    db.SupportMessages.Add(newMsg);
                    ticket.UpdatedAt = DateTime.UtcNow;
                    await db.SaveChangesAsync(ct);
                    _logger.LogInformation($"Received reply for ticket {ticket.Id}");
                    await NotifyApiAsync(ticket.Id, newMsg.AuthorName ?? "Admin", newMsg.Text, ct);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in HandleUpdateAsync");
            }
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