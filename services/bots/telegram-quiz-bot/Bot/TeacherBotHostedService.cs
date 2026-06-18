using System.Globalization;
using System.Net;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using TelegramQuizBot.Configuration;
using TelegramQuizBot.Services;
using TelegramQuizBot.Storage;

namespace TelegramQuizBot.Bot;

public sealed partial class TeacherBotHostedService : BackgroundService
{
    private readonly IServiceProvider _provider;
    private readonly ILogger<TeacherBotHostedService> _logger;
    private readonly TelegramQuizOptions _options;
    private readonly TeacherBotStateStore _state;
    private readonly TelegramBotClientFactory _clientFactory;
    private TelegramBotClient? _bot;

    public TeacherBotHostedService(
        IServiceProvider provider,
        ILogger<TeacherBotHostedService> logger,
        IOptions<TelegramQuizOptions> options,
        TeacherBotStateStore state,
        TelegramBotClientFactory clientFactory)
    {
        _provider = provider;
        _logger = logger;
        _options = options.Value;
        _state = state;
        _clientFactory = clientFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        TelegramDebugTrace.Write("teacher", "execute:start", ("tokenConfigured", !string.IsNullOrWhiteSpace(_options.TeacherBotToken)));

        if (string.IsNullOrWhiteSpace(_options.TeacherBotToken))
        {
            _logger.LogWarning("Teacher bot token is empty. Teacher bot is disabled.");
            TelegramDebugTrace.Write("teacher", "execute:disabled", ("reason", "empty-token"));
            return;
        }

        _bot = _clientFactory.Create(_options.TeacherBotToken);
        await _bot.DeleteWebhookAsync(cancellationToken: stoppingToken);
        _bot.StartReceiving(HandleUpdateAsync, HandleErrorAsync, new ReceiverOptions { AllowedUpdates = Array.Empty<UpdateType>() }, stoppingToken);
        _logger.LogInformation("Teacher Telegram bot started");
        TelegramDebugTrace.Write("teacher", "execute:started");

        await Task.Delay(Timeout.Infinite, stoppingToken);
    }

    private async Task HandleUpdateAsync(ITelegramBotClient bot, Update update, CancellationToken ct)
    {
        TelegramDebugTrace.Write(
            "teacher.update",
            "received",
            ("updateId", update.Id),
            ("type", update.Type),
            ("messageId", update.Message?.MessageId),
            ("callbackId", update.CallbackQuery?.Id));

        if (update.Message is { } message)
        {
            if (message.Chat.Type != ChatType.Private)
            {
                TelegramDebugTrace.Write("teacher.update", "ignored:non-private-message", ("chatId", message.Chat.Id), ("chatType", message.Chat.Type));
                return;
            }
            await HandleMessageAsync(bot, message, ct);
        }
        else if (update.CallbackQuery is { } callback)
        {
            await HandleCallbackAsync(bot, callback, ct);
        }
    }

}
