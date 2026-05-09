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

public sealed class StudentBotHostedService : BackgroundService
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
        if (string.IsNullOrWhiteSpace(_options.StudentBotToken))
        {
            _logger.LogWarning("Student bot token is empty. Student bot is disabled.");
            return;
        }

        _bot = _clientFactory.Create(_options.StudentBotToken);
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
        else if (update.CallbackQuery is { } callback)
        {
            if (callback.Message?.Chat.Type != ChatType.Private) return;
            await HandleCallbackAsync(bot, callback, ct);
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
        var directory = scope.ServiceProvider.GetRequiredService<StudentDirectoryService>();
        var access = scope.ServiceProvider.GetRequiredService<StudentAccessService>();
        var breaks = scope.ServiceProvider.GetRequiredService<TechnicalBreakService>();
        var quizzes = scope.ServiceProvider.GetRequiredService<QuizService>();
        var progress = scope.ServiceProvider.GetRequiredService<ProgressService>();

        await directory.RememberMessageAsync(message, ct);
        await LogStartAsync(db, message, ct);

        if (!await access.HasAccessAsync(userId, ct))
        {
            await bot.SendTextMessageAsync(
                message.Chat.Id,
                $"⛔ У вас пока нет доступа. Обратитесь к учителю.\n\nВаш Telegram ID: `{userId}`",
                parseMode: ParseMode.Markdown,
                cancellationToken: ct);
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

        var text = message.Text?.Trim() ?? string.Empty;

        if (_state.PendingTextAnswers.TryGetValue(userId, out var pending) && !string.IsNullOrWhiteSpace(text) && !text.StartsWith('/'))
        {
            var correct = string.Equals(Normalize(text), Normalize(pending.Answer), StringComparison.OrdinalIgnoreCase);
            await progress.SaveAnswerAsync(userId, message.From?.Username, pending, correct, ct);
            _state.PendingTextAnswers.TryRemove(userId, out _);

            var feedback = correct
                ? "🎉 Правильно! Молодец!"
                : $"❌ Неправильно, но ты справился! ✨\n\nПравильный ответ: <b>{Html(pending.Answer)}</b>\n{Html(EmptyToMissing(pending.Explanation))}";

            await bot.SendTextMessageAsync(
                message.Chat.Id,
                feedback,
                parseMode: ParseMode.Html,
                replyMarkup: AfterAnswerKeyboard(correct),
                cancellationToken: ct);
            return;
        }

        if (IsStartCommand(text))
        {
            await EnsureSettingsAsync(db, userId, ct);
            await SendStudentHomeAsync(bot, message.Chat.Id, ct);
            return;
        }

        if (text == "/help")
        {
            await SendStudentHomeAsync(bot, message.Chat.Id, ct);
            return;
        }

        if (text == "/stats" || text == "📊 Статистика")
        {
            await SendStudentStatsAsync(bot, message.Chat.Id, userId, progress, quizzes, ct);
            return;
        }

        if (text == "/next" || text == "➡️ Следующее задание" || text == "➡️ Следующая викторина" || text == "🎯 Задание")
        {
            await SendNextQuizAsync(bot, message.Chat.Id, userId, quizzes, db, ct);
            return;
        }

        if (text == "🔄 Сменить режим" || text == "🏠 Меню")
        {
            await SendStudentHomeAsync(bot, message.Chat.Id, ct);
            return;
        }

        await SendStudentHomeAsync(bot, message.Chat.Id, ct);
    }

    private async Task HandleCallbackAsync(ITelegramBotClient bot, CallbackQuery callback, CancellationToken ct)
    {
        var userId = callback.From.Id;
        var chatId = callback.Message?.Chat.Id ?? userId;
        var data = callback.Data ?? string.Empty;

        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TelegramQuizDbContext>();
        var access = scope.ServiceProvider.GetRequiredService<StudentAccessService>();
        var breaks = scope.ServiceProvider.GetRequiredService<TechnicalBreakService>();
        var quizzes = scope.ServiceProvider.GetRequiredService<QuizService>();
        var progress = scope.ServiceProvider.GetRequiredService<ProgressService>();

        await bot.AnswerCallbackQueryAsync(callback.Id, cancellationToken: ct);

        if (!await access.HasAccessAsync(userId, ct))
        {
            await bot.SendTextMessageAsync(chatId, $"⛔ У вас пока нет доступа. Обратитесь к учителю.\n\nВаш Telegram ID: `{userId}`", cancellationToken: ct);
            return;
        }

        if (await breaks.IsActiveAsync(ct))
        {
            await bot.SendTextMessageAsync(chatId, "🛠️ Сейчас технический перерыв. Попробуйте позже.", cancellationToken: ct);
            return;
        }

        if (data == "st:menu")
        {
            await SendStudentHomeAsync(bot, chatId, ct);
            return;
        }

        if (data == "st:stats")
        {
            await SendStudentStatsAsync(bot, chatId, userId, progress, quizzes, ct);
            return;
        }

        if (data == "st:next")
        {
            await SendNextQuizAsync(bot, chatId, userId, quizzes, db, ct);
            return;
        }

        if (data == "st:ask")
        {
            await bot.SendTextMessageAsync(
                chatId,
                "❓ Пока отдельный диалог с учителем не подключён. Напиши свой вопрос обычным сообщением учителю или попроси доступ через преподавательского бота.",
                replyMarkup: AfterAnswerKeyboard(false),
                cancellationToken: ct);
            return;
        }

        if (data == "st:mode:normal")
        {
            var settings = await EnsureSettingsAsync(db, userId, ct);
            settings.LearningMode = ModeNormal;
            settings.SelectedCategory = AllCategories;
            await db.SaveChangesAsync(ct);
            await SendCategoryPickerAsync(bot, chatId, quizzes, ct);
            return;
        }

        if (data == "st:mode:smart")
        {
            var settings = await EnsureSettingsAsync(db, userId, ct);
            settings.LearningMode = ModeSmart;
            settings.SelectedCategory = AllCategories;
            await db.SaveChangesAsync(ct);
            await SendSmartRecommendationsAsync(bot, chatId, userId, quizzes, ct);
            return;
        }

        if (data == "st:cat:random")
        {
            var settings = await EnsureSettingsAsync(db, userId, ct);
            settings.LearningMode = ModeNormal;
            settings.SelectedCategory = AllCategories;
            await db.SaveChangesAsync(ct);
            await bot.SendTextMessageAsync(chatId, "🎲 Случайная категория. Подбираю задание из всей базы.", cancellationToken: ct);
            await SendNextQuizAsync(bot, chatId, userId, quizzes, db, ct);
            return;
        }

        if (data.StartsWith("st:cat:", StringComparison.Ordinal))
        {
            var category = data[7..];
            if (string.IsNullOrWhiteSpace(category)) category = AllCategories;

            var settings = await EnsureSettingsAsync(db, userId, ct);
            settings.LearningMode = ModeNormal;
            settings.SelectedCategory = category;
            await db.SaveChangesAsync(ct);
            await bot.SendTextMessageAsync(chatId, $"📚 Категория: <b>{Html(category)}</b>", parseMode: ParseMode.Html, cancellationToken: ct);
            await SendNextQuizAsync(bot, chatId, userId, quizzes, db, ct);
            return;
        }

        await SendStudentHomeAsync(bot, chatId, ct);
    }

    private async Task SendNextQuizAsync(ITelegramBotClient bot, long chatId, long userId, QuizService quizzes, TelegramQuizDbContext db, CancellationToken ct)
    {
        var settings = await EnsureSettingsAsync(db, userId, ct);
        string? preferredSubcategory = null;

        if (settings.LearningMode == ModeSmart)
            preferredSubcategory = (await quizzes.GetWeakSubcategoriesAsync(userId, ct)).FirstOrDefault();

        var next = await quizzes.GetNextForUserAsync(userId, settings.SelectedCategory, preferredSubcategory, ct);
        var quiz = next.Quiz;
        if (quiz == null)
        {
            await bot.SendTextMessageAsync(
                chatId,
                "❌ Вопросов пока нет. База квизов пуста.",
                replyMarkup: StudentHomeKeyboard(),
                cancellationToken: ct);
            return;
        }

        if (next.FilterWasRelaxed)
        {
            await bot.SendTextMessageAsync(chatId, "ℹ️ В выбранной категории или теме вопросов не осталось, поэтому показываю задание из более широкого набора.", cancellationToken: ct);
        }
        else if (next.IsRepeatCycle)
        {
            await bot.SendTextMessageAsync(chatId, "🔄 Вы уже прошли все доступные вопросы по текущей категории. Начинаю новый круг.", cancellationToken: ct);
        }

        if (quiz.Type == "text")
        {
            _state.PendingTextAnswers[userId] = quiz;
            await bot.SendTextMessageAsync(
                chatId,
                $"📝 <b>{Html(quiz.Question)}</b>",
                parseMode: ParseMode.Html,
                cancellationToken: ct);
            return;
        }

        var options = quiz.GetOptions();
        if (options.Length == 0 || quiz.CorrectOptionId is null)
        {
            await bot.SendTextMessageAsync(chatId, $"⚠️ Вопрос {quiz.Id} повреждён: нет вариантов ответа.", replyMarkup: AfterAnswerKeyboard(false), cancellationToken: ct);
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
            _state.ActivePolls[pollId] = new ActivePollQuiz { UserId = userId, ChatId = chatId, Quiz = quiz };
    }

    private async Task HandlePollAnswerAsync(PollAnswer answer, CancellationToken ct)
    {
        if (!_state.ActivePolls.TryRemove(answer.PollId, out var active)) return;

        using var scope = _provider.CreateScope();
        var progress = scope.ServiceProvider.GetRequiredService<ProgressService>();
        var selected = answer.OptionIds.FirstOrDefault();
        var correct = active.Quiz.CorrectOptionId == selected;
        await progress.SaveAnswerAsync(answer.User.Id, answer.User.Username, active.Quiz, correct, ct);

        if (_bot == null) return;

        var text = correct
            ? "🎉 Правильно! Молодец!"
            : "❌ Неправильно, но ты справился! ✨";

        await _bot.SendTextMessageAsync(
            active.ChatId,
            text,
            replyMarkup: AfterAnswerKeyboard(correct),
            cancellationToken: ct);
    }

    private static async Task<UserSetting> EnsureSettingsAsync(TelegramQuizDbContext db, long userId, CancellationToken ct)
    {
        var settings = await db.UserSettings.FindAsync([userId], ct);
        if (settings != null) return settings;
        settings = new UserSetting { UserId = userId, LearningMode = ModeNormal, SelectedCategory = AllCategories };
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

    private async Task SendStudentStatsAsync(
        ITelegramBotClient bot,
        long chatId,
        long userId,
        ProgressService progress,
        QuizService quizzes,
        CancellationToken ct)
    {
        var p = await progress.GetProgressAsync(userId, ct);
        var recommendations = await quizzes.GetSmartRecommendationsAsync(userId, 5, ct);

        var lines = new List<string>
        {
            "<b>📊 Ваша статистика</b>",
            $"Всего: <b>{p.Total}</b>",
            $"✅ Правильно: <b>{p.Correct}</b>",
            $"❌ Ошибок: <b>{p.Incorrect}</b>",
            $"⭐ Опыт: <b>{p.Experience}</b>",
            string.Empty,
            "<b>💡 Темы для тренировки:</b>"
        };

        foreach (var (item, index) in recommendations.Select((item, index) => (item, index + 1)))
        {
            lines.Add($"{index}. {Html(item.Subcategory)} — точность {item.Accuracy:0.#}% ({item.Correct}/{item.Total})");
        }

        if (recommendations.Count == 0)
            lines.Add("Пока нет статистики. Решите несколько заданий, и я подберу слабые темы.");

        await bot.SendTextMessageAsync(
            chatId,
            string.Join('\n', lines),
            parseMode: ParseMode.Html,
            replyMarkup: StudentHomeKeyboard(),
            cancellationToken: ct);
    }

    private static async Task SendStudentHomeAsync(ITelegramBotClient bot, long chatId, CancellationToken ct)
    {
        await bot.SendTextMessageAsync(
            chatId,
            "👋 <b>Добро пожаловать в бот-викторину!</b>\n\nВыберите режим обучения:",
            parseMode: ParseMode.Html,
            replyMarkup: StudentHomeKeyboard(),
            cancellationToken: ct);
    }

    private static InlineKeyboardMarkup StudentHomeKeyboard()
    {
        return new InlineKeyboardMarkup(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("💡 Умные рекомендации", "st:mode:smart"),
                InlineKeyboardButton.WithCallbackData("📚 Обычный режим", "st:mode:normal")
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData("📊 Статистика", "st:stats"),
                InlineKeyboardButton.WithCallbackData("🎯 Задание", "st:next")
            }
        });
    }

    private static InlineKeyboardMarkup AfterAnswerKeyboard(bool correct)
    {
        return new InlineKeyboardMarkup(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData(correct ? "➡️ Следующее задание" : "➡️ Следующая викторина", "st:next"),
                InlineKeyboardButton.WithCallbackData("🔄 Сменить режим", "st:menu")
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData("❓ Задать вопрос", "st:ask"),
                InlineKeyboardButton.WithCallbackData("📊 Статистика", "st:stats")
            }
        });
    }

    private static async Task SendCategoryPickerAsync(ITelegramBotClient bot, long chatId, QuizService quizzes, CancellationToken ct)
    {
        var categories = await quizzes.GetCategoryStatsAsync(ct);
        var rows = new List<InlineKeyboardButton[]>();

        foreach (var chunk in categories.Chunk(2))
        {
            rows.Add(chunk
                .Select(c => InlineKeyboardButton.WithCallbackData($"{c.Name} ({c.Count})", $"st:cat:{c.Name}"))
                .ToArray());
        }

        rows.Add(new[] { InlineKeyboardButton.WithCallbackData("🎲 Случайная категория", "st:cat:random") });
        rows.Add(new[] { InlineKeyboardButton.WithCallbackData("🏠 Назад к режимам", "st:menu") });

        await bot.SendTextMessageAsync(
            chatId,
            "📚 <b>Обычный режим</b>\n\nВыберите категорию:",
            parseMode: ParseMode.Html,
            replyMarkup: new InlineKeyboardMarkup(rows),
            cancellationToken: ct);
    }

    private static async Task SendSmartRecommendationsAsync(ITelegramBotClient bot, long chatId, long userId, QuizService quizzes, CancellationToken ct)
    {
        var recommendations = await quizzes.GetSmartRecommendationsAsync(userId, 5, ct);
        var lines = new List<string>
        {
            "💡 <b>Умные рекомендации включены</b>",
            "",
            "На основе вашей статистики я буду чаще предлагать вопросы по этим темам:"
        };

        if (recommendations.Count == 0)
        {
            lines.Add("Пока статистики нет. Начнём с разных тем, а затем бот сам найдёт слабые места.");
        }
        else
        {
            foreach (var (item, index) in recommendations.Select((item, index) => (item, index + 1)))
            {
                lines.Add($"{index}. {Html(item.Subcategory)} (точность: {item.Accuracy:0.#}%, вопросов: {item.QuestionCount})");
            }
        }

        await bot.SendTextMessageAsync(
            chatId,
            string.Join('\n', lines),
            parseMode: ParseMode.Html,
            replyMarkup: new InlineKeyboardMarkup(new[]
            {
                new[] { InlineKeyboardButton.WithCallbackData("🎯 Начать тренировку", "st:next") },
                new[] { InlineKeyboardButton.WithCallbackData("📚 Обычный режим", "st:mode:normal"), InlineKeyboardButton.WithCallbackData("📊 Статистика", "st:stats") }
            }),
            cancellationToken: ct);
    }

    private Task HandleErrorAsync(ITelegramBotClient bot, Exception exception, CancellationToken ct)
    {
        if (TelegramPollingErrorClassifier.IsExpectedLongPollingTimeout(exception))
        {
            _logger.LogDebug("Student bot long polling timeout");
            return Task.CompletedTask;
        }

        if (TelegramPollingErrorClassifier.IsTransientTelegramApiError(exception))
        {
            _logger.LogWarning("Student bot transient Telegram polling error: {Message}", exception.Message);
            return Task.Delay(TimeSpan.FromSeconds(5), ct);
        }

        _logger.LogError(exception, "Student bot polling error");
        return Task.CompletedTask;
    }

    private static bool IsStartCommand(string text)
    {
        return string.Equals(text, "/start", StringComparison.OrdinalIgnoreCase)
               || string.Equals(text, "/menu", StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string value) => value.Trim().Replace("ё", "е", StringComparison.OrdinalIgnoreCase);

    private static string Html(string? value) => System.Net.WebUtility.HtmlEncode(value ?? string.Empty);

    private static string EmptyToMissing(string? value) => string.IsNullOrWhiteSpace(value) ? "Объяснение отсутствует." : value.Trim();
}
