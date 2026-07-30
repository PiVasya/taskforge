using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace TaskForge.SupportBot;

public sealed class Worker(ILogger<Worker> logger, IHttpClientFactory httpClientFactory, IConfiguration cfg) : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private TelegramBotClient? _bot;
    private long _supportGroupId;

    public bool TelegramReady => _bot != null;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var token = FirstNonEmpty(
            cfg["Telegram:BotToken"],
            cfg["SUPPORT_BOT_TOKEN"],
            cfg["TELEGRAM_BOT_TOKEN"]);
        var groupIdRaw = FirstNonEmpty(
            cfg["Telegram:SupportGroupId"],
            cfg["SupportBot:GroupId"],
            cfg["SUPPORT_BOT_GROUP_ID"],
            cfg["TELEGRAM_SUPPORT_GROUP_ID"]);

        if (string.IsNullOrWhiteSpace(token))
        {
            logger.LogWarning("Support bot is disabled: Telegram bot token is not configured.");
            await WaitUntilCancelledAsync(stoppingToken);
            return;
        }

        if (!long.TryParse(groupIdRaw, out _supportGroupId) || _supportGroupId == 0)
        {
            _supportGroupId = 0;
            logger.LogWarning("Telegram support group id is not configured or invalid. Private Telegram features remain available, but support group relay is disabled.");
        }

        _bot = new TelegramBotClient(token);
        try
        {
            await _bot.DeleteWebhookAsync(cancellationToken: stoppingToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to delete Telegram webhook before polling start.");
        }

        var receiverOptions = new ReceiverOptions { AllowedUpdates = Array.Empty<UpdateType>() };
        _bot.StartReceiving(HandleUpdateAsync, HandleErrorAsync, receiverOptions, cancellationToken: stoppingToken);
        logger.LogInformation("Support Telegram bot started. Support group id: {GroupId}", _supportGroupId);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SendPendingUserMessagesToGroupAsync(stoppingToken);
                await ForwardPendingAdminRepliesToUsersAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Support bot polling iteration failed.");
            }

            await Task.Delay(TimeSpan.FromSeconds(PollIntervalSeconds()), stoppingToken);
        }
    }

    private async Task SendPendingUserMessagesToGroupAsync(CancellationToken ct)
    {
        if (_bot == null || _supportGroupId == 0) return;
        var support = SupportClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "api/internal/support/telegram/pending-user-messages");
        AddInternalKey(request);
        using var response = await support.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Failed to load pending support messages. Status: {Status}", response.StatusCode);
            return;
        }

        var messages = await response.Content.ReadFromJsonAsync<List<PendingUserMessageDto>>(JsonOptions, ct) ?? new();
        foreach (var msg in messages)
        {
            var text = FormatGroupUserMessage(msg);
            var sent = await _bot.SendTextMessageAsync(_supportGroupId, text, cancellationToken: ct);
            using var mark = new HttpRequestMessage(HttpMethod.Post, $"api/internal/support/telegram/messages/{msg.MessageId}/mark-sent")
            {
                Content = JsonContent.Create(new TelegramMarkMessageRequest(_supportGroupId, sent.MessageId), options: JsonOptions)
            };
            AddInternalKey(mark);
            using var markResponse = await support.SendAsync(mark, ct);
            if (!markResponse.IsSuccessStatusCode)
            {
                logger.LogWarning("Failed to mark support message {MessageId} as sent to Telegram. Status: {Status}", msg.MessageId, markResponse.StatusCode);
            }
        }
    }

    private async Task ForwardPendingAdminRepliesToUsersAsync(CancellationToken ct)
    {
        if (_bot == null) return;
        var support = SupportClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "api/internal/support/telegram/pending-admin-replies");
        AddInternalKey(request);
        using var response = await support.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Failed to load pending admin replies. Status: {Status}", response.StatusCode);
            return;
        }

        var replies = await response.Content.ReadFromJsonAsync<List<PendingAdminReplyDto>>(JsonOptions, ct) ?? new();
        foreach (var reply in replies)
        {
            if (reply.UserId == null)
            {
                await MarkAdminReplyUnavailableAsync(reply.MessageId, ct);
                continue;
            }

            var contact = await LoadTelegramContactByUserIdAsync(reply.UserId.Value, ct);
            if (contact?.TelegramChatId == null)
            {
                await MarkAdminReplyUnavailableAsync(reply.MessageId, ct);
                continue;
            }

            var sent = await _bot.SendTextMessageAsync(contact.TelegramChatId.Value, FormatPrivateAdminReply(reply), cancellationToken: ct);
            using var mark = new HttpRequestMessage(HttpMethod.Post, $"api/internal/support/telegram/admin-replies/{reply.MessageId}/mark-delivered")
            {
                Content = JsonContent.Create(new TelegramMarkMessageRequest(contact.TelegramChatId.Value, sent.MessageId), options: JsonOptions)
            };
            AddInternalKey(mark);
            using var markResponse = await support.SendAsync(mark, ct);
            if (!markResponse.IsSuccessStatusCode)
            {
                logger.LogWarning("Failed to mark admin reply {MessageId} as delivered. Status: {Status}", reply.MessageId, markResponse.StatusCode);
            }
        }
    }


    public async Task<PasswordRecoveryDeliveryResult> SendPasswordRecoveryCodeAsync(
        PasswordRecoveryDeliveryRequest request,
        CancellationToken ct)
    {
        var bot = _bot;
        if (bot == null)
        {
            return PasswordRecoveryDeliveryResult.TelegramNotReady;
        }

        var lifetimeMinutes = Math.Clamp(request.LifetimeMinutes, 3, 30);
        var accountName = string.IsNullOrWhiteSpace(request.AccountName)
            ? "Пользователь TaskForge"
            : request.AccountName.Trim();
        var login = string.IsNullOrWhiteSpace(request.Login)
            ? "не указан"
            : request.Login.Trim();

        var text =
            "🔐 Восстановление аккаунта TaskForge\n\n" +
            $"Аккаунт: {accountName}\n" +
            $"Логин: {login}\n" +
            $"Код восстановления: {request.VerificationCode}\n\n" +
            $"Код действует {lifetimeMinutes} мин. Никому его не сообщайте.\n" +
            "Если вы не запрашивали восстановление, просто проигнорируйте это сообщение.";

        try
        {
            await bot.SendTextMessageAsync(
                request.TelegramChatId,
                text,
                cancellationToken: ct);
            logger.LogInformation("Password recovery code delivered by support-bot.");
            return PasswordRecoveryDeliveryResult.Success;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning("Password recovery Telegram delivery failed in support-bot. Exception type: {ExceptionType}", ex.GetType().Name);
            return PasswordRecoveryDeliveryResult.DeliveryFailed;
        }
    }

    private async Task HandleUpdateAsync(ITelegramBotClient bot, Update update, CancellationToken ct)
    {
        try
        {
            if (update.Message == null) return;
            var message = update.Message;

            if (message.Chat.Type == ChatType.Private)
            {
                await HandlePrivateMessageAsync(message, ct);
                return;
            }

            if (message.Chat.Id != _supportGroupId) return;
            if (message.ReplyToMessage == null) return;
            if (string.IsNullOrWhiteSpace(message.Text)) return;

            await HandleGroupReplyAsync(message, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Telegram update processing failed.");
        }
    }

    private async Task HandleGroupReplyAsync(Message message, CancellationToken ct)
    {
        var source = await LoadSupportMessageByTelegramIdAsync(message.ReplyToMessage!.MessageId, _supportGroupId, ct);
        if (source == null) return;

        var authorName = TelegramAuthorName(message);
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/internal/support/telegram/admin-reply")
        {
            Content = JsonContent.Create(new TelegramAdminReplyRequest(
                source.TicketId,
                authorName,
                message.Text,
                message.Chat.Id,
                message.MessageId,
                source.MessageId), options: JsonOptions)
        };
        AddInternalKey(request);
        using var response = await SupportClient().SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Failed to save Telegram admin reply for ticket {TicketId}. Status: {Status}", source.TicketId, response.StatusCode);
        }
    }

    private async Task HandlePrivateMessageAsync(Message msg, CancellationToken ct)
    {
        if (_bot == null) return;
        var text = (msg.Text ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(text)) return;

        if (text.StartsWith("/start", StringComparison.OrdinalIgnoreCase) || text.StartsWith("/help", StringComparison.OrdinalIgnoreCase))
        {
            await _bot.SendTextMessageAsync(msg.Chat.Id,
                "Привет! Я бот поддержки TaskForge.\n\n" +
                "Привязка Telegram:\n" +
                "1) TaskForge -> Профиль -> Интеграции -> Telegram -> Сгенерировать код\n" +
                "2) Отправь мне: /link ТВОЙ_КОД\n" +
                "или просто пришли код первым сообщением.\n\n" +
                "После привязки просто пиши сюда — сообщение уйдёт в единый чат поддержки.\n" +
                "Команды:\n" +
                "/link CODE — привязать Telegram\n" +
                "/help — помощь",
                cancellationToken: ct);
            return;
        }

        if (text.StartsWith("/link", StringComparison.OrdinalIgnoreCase))
        {
            var parts = text.Split(new[] { ' ', '\n', '\t', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                await _bot.SendTextMessageAsync(msg.Chat.Id, "Напиши так: /link ТВОЙ_КОД", cancellationToken: ct);
                return;
            }

            await TryLinkByCodeAsync(msg, parts[1], ct);
            return;
        }

        if (text.StartsWith("/new", StringComparison.OrdinalIgnoreCase))
        {
            text = string.Join(' ', text.Split(' ').Skip(1)).Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                await _bot.SendTextMessageAsync(msg.Chat.Id, "Просто напиши сообщение — оно попадёт в единый чат поддержки.", cancellationToken: ct);
                return;
            }
        }

        if (LooksLikeLinkCode(text) && await LoadTelegramContactByChatIdAsync(msg.Chat.Id, ct) == null)
        {
            await TryLinkByCodeAsync(msg, text, ct);
            return;
        }

        await HandleSupportMessageAsync(msg, text, ct);
    }

    private async Task TryLinkByCodeAsync(Message msg, string codeRaw, CancellationToken ct)
    {
        if (_bot == null) return;
        var normalized = (codeRaw ?? string.Empty).Trim().ToUpperInvariant();
        if (!LooksLikeLinkCode(normalized))
        {
            await _bot.SendTextMessageAsync(msg.Chat.Id,
                "Похоже, это не код привязки. Сгенерируй код на сайте и отправь мне: /link ТВОЙ_КОД",
                cancellationToken: ct);
            return;
        }

        var payload = new TelegramConfirmRequest(normalized, msg.Chat.Id, msg.From?.Username);
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/internal/integrations/telegram/confirm")
        {
            Content = JsonContent.Create(payload, options: JsonOptions)
        };
        AddInternalKey(request);

        try
        {
            using var response = await IdentityClient().SendAsync(request, ct);
            var dto = await response.Content.ReadFromJsonAsync<TelegramConfirmResponse>(JsonOptions, ct);
            var prefix = response.IsSuccessStatusCode && dto?.Ok == true ? "✅ " : "❌ ";
            await _bot.SendTextMessageAsync(msg.Chat.Id, prefix + (dto?.Message ?? "Не удалось привязать Telegram."), cancellationToken: ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Telegram link confirmation failed.");
            await _bot.SendTextMessageAsync(msg.Chat.Id, "Сервис сейчас недоступен. Попробуй позже.", cancellationToken: ct);
        }
    }

    private async Task HandleSupportMessageAsync(Message msg, string text, CancellationToken ct)
    {
        if (_bot == null) return;
        var contact = await LoadTelegramContactByChatIdAsync(msg.Chat.Id, ct);
        if (contact?.UserId == null)
        {
            await _bot.SendTextMessageAsync(msg.Chat.Id,
                "Я могу отправлять сообщения в поддержку только после привязки Telegram к TaskForge.\n\n" +
                "Сгенерируй код на сайте: Профиль -> Интеграции -> Telegram, затем отправь мне /link КОД.",
                cancellationToken: ct);
            return;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "api/internal/support/telegram/user-message")
        {
            Content = JsonContent.Create(new TelegramUserMessageRequest(contact.UserId.Value, text, false), options: JsonOptions)
        };
        AddInternalKey(request);
        using var response = await SupportClient().SendAsync(request, ct);
        if (response.IsSuccessStatusCode)
        {
            await _bot.SendTextMessageAsync(msg.Chat.Id,
                "✅ Сообщение отправлено в техподдержку.",
                cancellationToken: ct);
            return;
        }

        await _bot.SendTextMessageAsync(msg.Chat.Id, "Не удалось отправить сообщение в поддержку. Попробуй позже.", cancellationToken: ct);
    }

    private async Task<TelegramContactDto?> LoadTelegramContactByChatIdAsync(long chatId, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"api/internal/integrations/telegram/by-chat/{chatId}");
        AddInternalKey(request);
        using var response = await IdentityClient().SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (!response.IsSuccessStatusCode) return null;
        return await response.Content.ReadFromJsonAsync<TelegramContactDto>(JsonOptions, ct);
    }

    private async Task<TelegramContactDto?> LoadTelegramContactByUserIdAsync(Guid userId, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"api/internal/integrations/telegram/users/{userId}");
        AddInternalKey(request);
        using var response = await IdentityClient().SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (!response.IsSuccessStatusCode) return null;
        return await response.Content.ReadFromJsonAsync<TelegramContactDto>(JsonOptions, ct);
    }

    private async Task<SourceSupportMessageDto?> LoadSupportMessageByTelegramIdAsync(int telegramMessageId, long chatId, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"api/internal/support/telegram/messages/by-telegram/{telegramMessageId}?chatId={chatId}");
        AddInternalKey(request);
        using var response = await SupportClient().SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (!response.IsSuccessStatusCode) return null;
        return await response.Content.ReadFromJsonAsync<SourceSupportMessageDto>(JsonOptions, ct);
    }

    private async Task MarkAdminReplyUnavailableAsync(Guid messageId, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"api/internal/support/telegram/admin-replies/{messageId}/mark-unavailable");
        AddInternalKey(request);
        using var response = await SupportClient().SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Failed to mark admin reply {MessageId} as unavailable. Status: {Status}", messageId, response.StatusCode);
        }
    }

    private HttpClient SupportClient() => httpClientFactory.CreateClient("support-api");

    private HttpClient IdentityClient() => httpClientFactory.CreateClient("identity-api");

    private void AddInternalKey(HttpRequestMessage request)
    {
        var key = FirstNonEmpty(
            cfg["InternalApi:Key"],
            cfg["TaskForgeInternalApi:ApiKey"],
            cfg["TaskForge:InternalKey"],
            Environment.GetEnvironmentVariable("TASKFORGE_INTERNAL_KEY"));
        if (!string.IsNullOrWhiteSpace(key)) request.Headers.TryAddWithoutValidation("X-Internal-Key", key);
    }

    private int PollIntervalSeconds() => Math.Clamp(cfg.GetValue("SupportBot:PollIntervalSeconds", 10), 2, 300);

    private static string FormatGroupUserMessage(PendingUserMessageDto msg)
    {
        var user = msg.User?.DisplayName ?? msg.User?.Login ?? msg.User?.MaskedEmail ?? msg.User?.Email ?? "Пользователь";
        var login = string.IsNullOrWhiteSpace(msg.User?.Login) ? "—" : "@" + msg.User!.Login;
        return
            "💬 Новое сообщение в поддержке\n" +
            $"Источник: {SourceLabel(msg.Source)}\n" +
            $"Пользователь: {user}\n" +
            $"Логин: {login}\n" +
            $"Email: {msg.User?.MaskedEmail ?? msg.User?.Email ?? "—"}\n\n" +
            (msg.Text ?? string.Empty);
    }

    private static string SourceLabel(string? source) => source switch
    {
        "TelegramUser" => "Telegram",
        "Web" => "Сайт",
        _ => string.IsNullOrWhiteSpace(source) ? "Сайт" : source
    };

    private static string FormatPrivateAdminReply(PendingAdminReplyDto reply) =>
        "🛠️ Ответ поддержки\n\n" +
        (reply.Text ?? string.Empty);

    private static string TelegramAuthorName(Message message)
    {
        var firstName = message.From?.FirstName ?? string.Empty;
        var lastName = message.From?.LastName ?? string.Empty;
        var authorName = $"{firstName} {lastName}".Trim();
        return string.IsNullOrWhiteSpace(authorName) ? (message.From?.Username ?? "Admin") : authorName;
    }

    private static bool LooksLikeLinkCode(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.Trim();
        if (t.Length < 4 || t.Length > 32) return false;
        var hasAlphaNum = false;
        foreach (var ch in t)
        {
            if (char.IsLetterOrDigit(ch))
            {
                hasAlphaNum = true;
                continue;
            }
            if (ch == '-') continue;
            return false;
        }
        return hasAlphaNum;
    }

    private static string? FirstNonEmpty(params string?[] values)
        => values.Select(x => (x ?? string.Empty).Trim()).FirstOrDefault(x => x.Length > 0);

    private static async Task WaitUntilCancelledAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private Task HandleErrorAsync(ITelegramBotClient bot, Exception exception, CancellationToken ct)
    {
        var errorMessage = exception switch
        {
            ApiRequestException apiRequestException => $"Telegram API Error: [{apiRequestException.ErrorCode}] {apiRequestException.Message}",
            _ => exception.ToString()
        };
        logger.LogError("{Error}", errorMessage);
        return Task.CompletedTask;
    }

    private sealed class PendingUserMessageDto
    {
        public Guid MessageId { get; set; }
        public Guid TicketId { get; set; }
        public string? Subject { get; set; }
        public Guid? UserId { get; set; }
        public TelegramUserDto? User { get; set; }
        public string? Text { get; set; }
        public string? Source { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
    }

    private sealed class TelegramUserDto
    {
        public Guid UserId { get; set; }
        public string? Login { get; set; }
        public string? Email { get; set; }
        public string? MaskedEmail { get; set; }
        public string? DisplayName { get; set; }
    }

    private sealed class PendingAdminReplyDto
    {
        public Guid MessageId { get; set; }
        public Guid TicketId { get; set; }
        public Guid? UserId { get; set; }
        public string? Subject { get; set; }
        public string? Text { get; set; }
        public string? Source { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
    }

    private sealed class SourceSupportMessageDto
    {
        public Guid MessageId { get; set; }
        public Guid TicketId { get; set; }
    }

    private sealed class TelegramContactDto
    {
        public Guid? UserId { get; set; }
        public long? TelegramChatId { get; set; }
        public string? TelegramUsername { get; set; }
        public string? DisplayName { get; set; }
    }

    private sealed class TelegramConfirmResponse
    {
        public bool Ok { get; set; }
        public string? Message { get; set; }
    }

    private sealed record TelegramConfirmRequest(string Code, long ChatId, string? Username);

    private sealed record TelegramMarkMessageRequest(long ChatId, int MessageId);

    private sealed record TelegramUserMessageRequest(Guid UserId, string Message, bool ForceNewTicket);

    private sealed record TelegramAdminReplyRequest(Guid TicketId, string? AuthorName, string? Message, long? TelegramChatId, int? TelegramMessageId, Guid? ReplyToMessageId);
}
