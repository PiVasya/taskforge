using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using TelegramQuizBot.Configuration;
using TelegramQuizBot.Data;
using TelegramQuizBot.Data.Entities;
using TelegramQuizBot.Services;

namespace TelegramQuizBot.Bot;

public sealed class StudentBotHostedService : BackgroundService
{
    private readonly IServiceProvider _provider;
    private readonly ILogger<StudentBotHostedService> _logger;
    private readonly TelegramQuizOptions _options;
    private readonly StudentBotStateStore _state;
    private readonly TelegramBotClientFactory _botClientFactory;
    private TelegramBotClient? _bot;

    public StudentBotHostedService(
        IServiceProvider provider,
        ILogger<StudentBotHostedService> logger,
        IOptions<TelegramQuizOptions> options,
        StudentBotStateStore state,
        TelegramBotClientFactory botClientFactory)
    {
        _provider = provider;
        _logger = logger;
        _options = options.Value;
        _state = state;
        _botClientFactory = botClientFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(_options.StudentBotToken))
        {
            _logger.LogWarning("Student bot token is empty. Student bot is disabled.");
            return;
        }

        _bot = _botClientFactory.Create(_options.StudentBotToken);
        await _bot.DeleteWebhookAsync(cancellationToken: stoppingToken);
        _bot.StartReceiving(HandleUpdateAsync, HandleErrorAsync, new ReceiverOptions { AllowedUpdates = Array.Empty<UpdateType>() }, stoppingToken);
        _logger.LogInformation("Student Telegram bot started");

        await Task.Delay(Timeout.Infinite, stoppingToken);
    }

    private async Task HandleUpdateAsync(ITelegramBotClient bot, Update update, CancellationToken ct)
    {
        if (update.Message is { } message)
        {
            if (message.Chat.Type != ChatType.Private) return;
            await HandleMessageAsync(bot, message, ct);
        }
        else if (update.PollAnswer is { } answer)
        {
            await HandlePollAnswerAsync(answer, ct);
        }
    }

    private async Task HandleMessageAsync(ITelegramBotClient bot, Message message, CancellationToken ct)
    {
        var userId = message.From?.Id ?? 0;
        if (userId == 0) return;

        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TelegramQuizDbContext>();
        var access = scope.ServiceProvider.GetRequiredService<StudentAccessService>();
        var breaks = scope.ServiceProvider.GetRequiredService<TechnicalBreakService>();
        var quizzes = scope.ServiceProvider.GetRequiredService<QuizService>();
        var progress = scope.ServiceProvider.GetRequiredService<ProgressService>();

        await LogStartAsync(db, message, ct);

        if (!await access.HasAccessAsync(userId, ct))
        {
            await bot.SendTextMessageAsync(message.Chat.Id, "⛔ У вас нет доступа. Обратитесь к учителю.", cancellationToken: ct);
            return;
        }

        if (await breaks.IsActiveAsync(ct))
        {
            var br = await breaks.GetAsync(ct);
            await bot.SendTextMessageAsync(message.Chat.Id, br.EndTime is null
                ? "🛠️ Сейчас технический перерыв. Попробуйте позже."
                : $"🛠️ Сейчас технический перерыв до {br.EndTime:HH:mm}.", cancellationToken: ct);
            return;
        }

        var text = message.Text?.Trim();

        if (_state.PendingTextAnswers.TryGetValue(userId, out var pending) && !string.IsNullOrWhiteSpace(text) && !text.StartsWith('/'))
        {
            var correct = string.Equals(Normalize(text), Normalize(pending.Answer), StringComparison.OrdinalIgnoreCase);
            await progress.SaveAnswerAsync(userId, message.From?.Username, pending, correct, ct);
            _state.PendingTextAnswers.TryRemove(userId, out _);
            await bot.SendTextMessageAsync(message.Chat.Id, correct
                ? "✅ Верно!"
                : $"❌ Неверно. Правильный ответ: {pending.Answer}\n{pending.Explanation}", cancellationToken: ct);
            return;
        }

        if (text == "/start")
        {
            await EnsureSettingsAsync(db, userId, ct);
            await bot.SendTextMessageAsync(message.Chat.Id, "✅ Доступ подтверждён. Используйте /next для следующего вопроса или /stats для статистики.", cancellationToken: ct);
        }
        else if (text == "/help")
        {
            await bot.SendTextMessageAsync(message.Chat.Id, TelegramText.StudentHelp, cancellationToken: ct);
        }
        else if (text == "/stats")
        {
            var p = await progress.GetProgressAsync(userId, ct);
            await bot.SendTextMessageAsync(message.Chat.Id, $"📊 Ваша статистика:\nВсего: {p.Total}\n✅ Правильно: {p.Correct}\n❌ Ошибок: {p.Incorrect}\n⭐ Опыт: {p.Experience}", cancellationToken: ct);
        }
        else if (text == "/next")
        {
            await SendNextQuizAsync(bot, message.Chat.Id, userId, quizzes, db, ct);
        }
        else
        {
            await bot.SendTextMessageAsync(message.Chat.Id, "Используйте /next, /stats или /help.", cancellationToken: ct);
        }
    }

    private async Task SendNextQuizAsync(ITelegramBotClient bot, long chatId, long userId, QuizService quizzes, TelegramQuizDbContext db, CancellationToken ct)
    {
        var settings = await EnsureSettingsAsync(db, userId, ct);
        string? preferredSubcategory = null;

        if (settings.LearningMode == "smart")
            preferredSubcategory = (await quizzes.GetWeakSubcategoriesAsync(userId, ct)).FirstOrDefault();

        var quiz = await quizzes.GetRandomForUserAsync(userId, settings.SelectedCategory, preferredSubcategory, ct);
        if (quiz == null)
        {
            await bot.SendTextMessageAsync(chatId, "❌ Вопросов пока нет.", cancellationToken: ct);
            return;
        }

        if (quiz.Type == "text")
        {
            _state.PendingTextAnswers[userId] = quiz;
            await bot.SendTextMessageAsync(chatId, quiz.Question, cancellationToken: ct);
            return;
        }

        var options = quiz.GetOptions();
        if (options.Length == 0 || quiz.CorrectOptionId is null)
        {
            await bot.SendTextMessageAsync(chatId, $"⚠️ Вопрос {quiz.Id} повреждён: нет вариантов ответа.", cancellationToken: ct);
            return;
        }

        var sent = await bot.SendPollAsync(
            chatId: chatId,
            question: quiz.Question,
            options: options,
            type: PollType.Quiz,
            correctOptionId: quiz.CorrectOptionId,
            explanation: quiz.Explanation,
            isAnonymous: false,
            cancellationToken: ct);

        if (sent.Poll?.Id is { } pollId)
            _state.ActivePolls[pollId] = new ActivePollQuiz { UserId = userId, Quiz = quiz };
    }

    private async Task HandlePollAnswerAsync(PollAnswer answer, CancellationToken ct)
    {
        if (!_state.ActivePolls.TryRemove(answer.PollId, out var active)) return;

        using var scope = _provider.CreateScope();
        var progress = scope.ServiceProvider.GetRequiredService<ProgressService>();
        var selected = answer.OptionIds.FirstOrDefault();
        var correct = active.Quiz.CorrectOptionId == selected;
        await progress.SaveAnswerAsync(answer.User.Id, answer.User.Username, active.Quiz, correct, ct);
    }

    private static async Task<UserSetting> EnsureSettingsAsync(TelegramQuizDbContext db, long userId, CancellationToken ct)
    {
        var settings = await db.UserSettings.FindAsync([userId], ct);
        if (settings != null) return settings;
        settings = new UserSetting { UserId = userId, LearningMode = "normal", SelectedCategory = "all" };
        db.UserSettings.Add(settings);
        await db.SaveChangesAsync(ct);
        return settings;
    }

    private static async Task LogStartAsync(TelegramQuizDbContext db, Message message, CancellationToken ct)
    {
        if (message.Text != "/start") return;
        db.StartLog.Add(new StartLogEntry
        {
            UserId = message.From!.Id,
            Username = message.From.Username,
            FullName = string.Join(' ', new[] { message.From.FirstName, message.From.LastName }.Where(x => !string.IsNullOrWhiteSpace(x))),
            Timestamp = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync(ct);
    }

    private Task HandleErrorAsync(ITelegramBotClient bot, Exception exception, CancellationToken ct)
    {
        if (TelegramPollingErrorClassifier.IsExpectedShutdownOrLongPollingTimeout(exception, ct))
        {
            _logger.LogDebug("Student bot long polling timeout or shutdown signal");
            return Task.CompletedTask;
        }

        _logger.LogError(exception, "Student bot polling error");
        return Task.CompletedTask;
    }

    private static string Normalize(string value) => value.Trim().Replace("ё", "е", StringComparison.OrdinalIgnoreCase);
}
