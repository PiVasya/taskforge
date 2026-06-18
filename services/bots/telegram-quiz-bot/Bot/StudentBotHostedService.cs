using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using TelegramQuizBot.Configuration;
using TelegramQuizBot.Data;
using TelegramQuizBot.Data.Entities;
using TelegramQuizBot.Services;

namespace TelegramQuizBot.Bot;

public sealed partial class StudentBotHostedService : BackgroundService
{
    private readonly IServiceProvider _provider;
    private readonly ILogger<StudentBotHostedService> _logger;
    private readonly TelegramQuizOptions _options;
    private readonly StudentBotStateStore _state;
    private readonly TelegramBotClientFactory _clientFactory;
    private TelegramBotClient? _bot;

    private const string ModeNormal = "normal";
    private const string ModeSmart = "smart";
    private const string AllCategories = "all";

    public StudentBotHostedService(
        IServiceProvider provider,
        ILogger<StudentBotHostedService> logger,
        IOptions<TelegramQuizOptions> options,
        StudentBotStateStore state,
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
        TelegramDebugTrace.Write("student", "execute:start", ("tokenConfigured", !string.IsNullOrWhiteSpace(_options.StudentBotToken)));

        if (string.IsNullOrWhiteSpace(_options.StudentBotToken))
        {
            _logger.LogWarning("Student bot token is empty. Student bot is disabled.");
            TelegramDebugTrace.Write("student", "execute:disabled", ("reason", "empty-token"));
            return;
        }

        _bot = _clientFactory.Create(_options.StudentBotToken);
        await _bot.DeleteWebhookAsync(cancellationToken: stoppingToken);
        _bot.StartReceiving(HandleUpdateAsync, HandleErrorAsync, new ReceiverOptions { AllowedUpdates = Array.Empty<UpdateType>() }, stoppingToken);
        _logger.LogInformation("Student Telegram bot started");
        TelegramDebugTrace.Write("student", "execute:started");

        await Task.Delay(Timeout.Infinite, stoppingToken);
    }

    private async Task HandleUpdateAsync(ITelegramBotClient bot, Update update, CancellationToken ct)
    {
        TelegramDebugTrace.Write(
            "student.update",
            "received",
            ("updateId", update.Id),
            ("type", update.Type),
            ("messageId", update.Message?.MessageId),
            ("callbackId", update.CallbackQuery?.Id),
            ("pollId", update.PollAnswer?.PollId));

        if (update.Message is { } message)
        {
            if (message.Chat.Type != ChatType.Private)
            {
                TelegramDebugTrace.Write("student.update", "ignored:non-private-message", ("chatId", message.Chat.Id), ("chatType", message.Chat.Type));
                return;
            }
            await HandleMessageAsync(bot, message, ct);
        }
        else if (update.CallbackQuery is { } callback)
        {
            if (callback.Message?.Chat.Type != ChatType.Private)
            {
                TelegramDebugTrace.Write("student.update", "ignored:non-private-callback", ("userId", callback.From.Id), ("data", callback.Data));
                return;
            }
            await HandleCallbackAsync(bot, callback, ct);
        }
        else if (update.PollAnswer is { } answer)
        {
            await HandlePollAnswerAsync(answer, ct);
        }
    }

}
