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

    private async Task HandleMessageAsync(ITelegramBotClient bot, Message message, CancellationToken ct)
    {
        var userId = message.From?.Id ?? 0;
        TelegramDebugTrace.Write(
            "student.message",
            "received",
            ("messageId", message.MessageId),
            ("chatId", message.Chat.Id),
            ("userId", userId),
            ("username", message.From?.Username),
            ("firstName", message.From?.FirstName),
            ("lastName", message.From?.LastName),
            ("text", message.Text),
            ("type", message.Type));

        if (userId == 0)
        {
            TelegramDebugTrace.Write("student.message", "ignored:no-user");
            return;
        }

        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TelegramQuizDbContext>();
        var directory = scope.ServiceProvider.GetRequiredService<StudentDirectoryService>();
        var access = scope.ServiceProvider.GetRequiredService<StudentAccessService>();
        var breaks = scope.ServiceProvider.GetRequiredService<TechnicalBreakService>();
        var quizzes = scope.ServiceProvider.GetRequiredService<QuizService>();
        var progress = scope.ServiceProvider.GetRequiredService<ProgressService>();

        await directory.RememberMessageAsync(message, ct);
        TelegramDebugTrace.Write("student.message", "contact-remembered", ("userId", userId));
        await LogStartAsync(db, message, ct);

        var hasAccess = await access.HasAccessAsync(userId, ct);
        TelegramDebugTrace.Write("student.message", "access-check", ("userId", userId), ("hasAccess", hasAccess));
        if (!hasAccess)
        {
            TelegramDebugTrace.Write("student.message", "blocked:no-access", ("userId", userId));
            await bot.SendTextMessageAsync(
                message.Chat.Id,
                $"⛔ У вас пока нет доступа. Обратитесь к учителю.\n\nВаш Telegram ID: `{userId}`",
                parseMode: ParseMode.Markdown,
                cancellationToken: ct);
            return;
        }

        var technicalBreakActive = await breaks.IsActiveAsync(ct);
        TelegramDebugTrace.Write("student.message", "technical-break-check", ("active", technicalBreakActive));
        if (technicalBreakActive)
        {
            var br = await breaks.GetAsync(ct);
            TelegramDebugTrace.Write("student.message", "blocked:technical-break", ("endTime", br.EndTime));
            await bot.SendTextMessageAsync(message.Chat.Id, br.EndTime is null
                ? "🛠️ Сейчас технический перерыв. Попробуйте позже."
                : $"🛠️ Сейчас технический перерыв до {br.EndTime:HH:mm}.", cancellationToken: ct);
            return;
        }

        var text = message.Text?.Trim() ?? string.Empty;

        if (_state.PendingTextAnswers.TryGetValue(userId, out var pending) && !string.IsNullOrWhiteSpace(text) && !text.StartsWith('/'))
        {
            var correct = string.Equals(Normalize(text), Normalize(pending.Answer), StringComparison.OrdinalIgnoreCase);
            TelegramDebugTrace.Write(
                "student.answer",
                "text-answer",
                ("userId", userId),
                ("quizId", pending.Id),
                ("input", text),
                ("expected", pending.Answer),
                ("correct", correct));
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
            TelegramDebugTrace.Write("student.message", "command:start", ("userId", userId));
            await EnsureSettingsAsync(db, userId, ct);
            await SendStudentHomeAsync(bot, message.Chat.Id, ct);
            return;
        }

        if (text == "/help")
        {
            TelegramDebugTrace.Write("student.message", "command:help", ("userId", userId));
            await SendStudentHomeAsync(bot, message.Chat.Id, ct);
            return;
        }

        if (text == "/stats" || text == "📊 Статистика")
        {
            TelegramDebugTrace.Write("student.message", "command:stats", ("userId", userId), ("text", text));
            await SendStudentStatsAsync(bot, message.Chat.Id, userId, progress, quizzes, includeAllTopics: false, ct);
            return;
        }

        if (text == "/next" || text == "➡️ Следующее задание" || text == "➡️ Следующая викторина" || text == "🎯 Задание")
        {
            TelegramDebugTrace.Write("student.message", "command:next", ("userId", userId), ("text", text));
            await SendNextQuizAsync(bot, message.Chat.Id, userId, quizzes, db, ct);
            return;
        }

        if (text == "🔄 Сменить режим" || text == "🏠 Меню")
        {
            TelegramDebugTrace.Write("student.message", "command:menu", ("userId", userId), ("text", text));
            await SendStudentHomeAsync(bot, message.Chat.Id, ct);
            return;
        }

        TelegramDebugTrace.Write("student.message", "fallback:home", ("userId", userId), ("text", text));
        await SendStudentHomeAsync(bot, message.Chat.Id, ct);
    }

    private async Task HandleCallbackAsync(ITelegramBotClient bot, CallbackQuery callback, CancellationToken ct)
    {
        var userId = callback.From.Id;
        var chatId = callback.Message?.Chat.Id ?? userId;
        var data = callback.Data ?? string.Empty;
        TelegramDebugTrace.Write(
            "student.callback",
            "received",
            ("callbackId", callback.Id),
            ("chatId", chatId),
            ("userId", userId),
            ("username", callback.From.Username),
            ("messageId", callback.Message?.MessageId),
            ("data", data));

        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TelegramQuizDbContext>();
        var access = scope.ServiceProvider.GetRequiredService<StudentAccessService>();
        var breaks = scope.ServiceProvider.GetRequiredService<TechnicalBreakService>();
        var quizzes = scope.ServiceProvider.GetRequiredService<QuizService>();
        var progress = scope.ServiceProvider.GetRequiredService<ProgressService>();

        await bot.SafeAnswerCallbackQueryAsync(callback.Id, _logger, cancellationToken: ct);
        TelegramDebugTrace.Write("student.callback", "ack-finished", ("callbackId", callback.Id), ("data", data));

        var hasAccess = await access.HasAccessAsync(userId, ct);
        TelegramDebugTrace.Write("student.callback", "access-check", ("userId", userId), ("hasAccess", hasAccess), ("data", data));
        if (!hasAccess)
        {
            TelegramDebugTrace.Write("student.callback", "blocked:no-access", ("userId", userId), ("data", data));
            await bot.SendTextMessageAsync(chatId, $"⛔ У вас пока нет доступа. Обратитесь к учителю.\n\nВаш Telegram ID: `{userId}`", cancellationToken: ct);
            return;
        }

        var technicalBreakActive = await breaks.IsActiveAsync(ct);
        TelegramDebugTrace.Write("student.callback", "technical-break-check", ("active", technicalBreakActive), ("data", data));
        if (technicalBreakActive)
        {
            TelegramDebugTrace.Write("student.callback", "blocked:technical-break", ("userId", userId), ("data", data));
            await bot.SendTextMessageAsync(chatId, "🛠️ Сейчас технический перерыв. Попробуйте позже.", cancellationToken: ct);
            return;
        }

        if (data == "st:menu")
        {
            TelegramDebugTrace.Write("student.callback", "callback:menu", ("userId", userId), ("data", data));
            await SendStudentHomeAsync(bot, chatId, ct);
            return;
        }

        if (data == "st:stats" || data == "st:stats:solved" || data == "st:stats:all")
        {
            var includeAllTopics = data == "st:stats:all";
            TelegramDebugTrace.Write("student.callback", "callback:stats", ("userId", userId), ("data", data), ("includeAllTopics", includeAllTopics));
            await SendStudentStatsAsync(bot, chatId, userId, progress, quizzes, includeAllTopics, ct);
            return;
        }

        if (data == "st:next")
        {
            TelegramDebugTrace.Write("student.callback", "callback:next", ("userId", userId), ("data", data));
            await SendNextQuizAsync(bot, chatId, userId, quizzes, db, ct);
            return;
        }

        if (data == "st:ask")
        {
            TelegramDebugTrace.Write("student.callback", "callback:ask", ("userId", userId), ("data", data));
            await bot.SendTextMessageAsync(
                chatId,
                "❓ Пока отдельный диалог с учителем не подключён. Напиши свой вопрос обычным сообщением учителю или попроси доступ через преподавательского бота.",
                replyMarkup: AfterAnswerKeyboard(false),
                cancellationToken: ct);
            return;
        }

        if (data == "st:mode:normal")
        {
            TelegramDebugTrace.Write("student.callback", "callback:mode-normal", ("userId", userId), ("data", data));
            var settings = await EnsureSettingsAsync(db, userId, ct);
            settings.LearningMode = ModeNormal;
            settings.SelectedCategory = AllCategories;
            await db.SaveChangesAsync(ct);
            await SendCategoryPickerAsync(bot, chatId, quizzes, ct);
            return;
        }

        if (data == "st:mode:smart")
        {
            TelegramDebugTrace.Write("student.callback", "callback:mode-smart", ("userId", userId), ("data", data));
            var settings = await EnsureSettingsAsync(db, userId, ct);
            settings.LearningMode = ModeSmart;
            settings.SelectedCategory = AllCategories;
            await db.SaveChangesAsync(ct);
            await SendSmartRecommendationsAsync(bot, chatId, userId, quizzes, ct);
            return;
        }

        if (data == "st:cat:random")
        {
            TelegramDebugTrace.Write("student.callback", "callback:cat-random", ("userId", userId), ("data", data));
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
            TelegramDebugTrace.Write("student.callback", "callback:category", ("userId", userId), ("data", data), ("category", category));
            if (string.IsNullOrWhiteSpace(category)) category = AllCategories;

            var settings = await EnsureSettingsAsync(db, userId, ct);
            settings.LearningMode = ModeNormal;
            settings.SelectedCategory = category;
            await db.SaveChangesAsync(ct);
            await bot.SendTextMessageAsync(chatId, $"📚 Категория: <b>{Html(category)}</b>", parseMode: ParseMode.Html, cancellationToken: ct);
            await SendNextQuizAsync(bot, chatId, userId, quizzes, db, ct);
            return;
        }

        TelegramDebugTrace.Write("student.callback", "fallback:home", ("userId", userId), ("data", data));
        await SendStudentHomeAsync(bot, chatId, ct);
    }

    private async Task SendNextQuizAsync(ITelegramBotClient bot, long chatId, long userId, QuizService quizzes, TelegramQuizDbContext db, CancellationToken ct)
    {
        var settings = await EnsureSettingsAsync(db, userId, ct);
        TelegramDebugTrace.Write(
            "student.quiz",
            "next:start",
            ("chatId", chatId),
            ("userId", userId),
            ("mode", settings.LearningMode),
            ("selectedCategory", settings.SelectedCategory));
        string? preferredSubcategory = null;

        if (settings.LearningMode == ModeSmart)
        {
            var weakSubcategories = await quizzes.GetWeakSubcategoriesAsync(userId, ct);
            preferredSubcategory = weakSubcategories.FirstOrDefault();
            TelegramDebugTrace.Write("student.quiz", "smart:weak-subcategories", ("userId", userId), ("preferred", preferredSubcategory), ("all", string.Join(",", weakSubcategories)));
        }

        var next = await quizzes.GetNextForUserAsync(userId, settings.SelectedCategory, preferredSubcategory, ct);
        TelegramDebugTrace.Write(
            "student.quiz",
            "next:result",
            ("userId", userId),
            ("quizId", next.Quiz?.Id),
            ("totalAvailable", next.TotalAvailable),
            ("unseenAvailable", next.UnseenAvailable),
            ("repeatCycle", next.IsRepeatCycle),
            ("filterRelaxed", next.FilterWasRelaxed),
            ("preferredSubcategory", preferredSubcategory));
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
            TelegramDebugTrace.Write("student.quiz", "send:text", ("chatId", chatId), ("userId", userId), ("quizId", quiz.Id), ("category", quiz.Category), ("subcategory", quiz.Subcategory), ("question", quiz.Question));
            _state.PendingTextAnswers[userId] = quiz;
            await bot.SendTextMessageAsync(
                chatId,
                BuildQuizTopicHeader(quiz, settings, next, preferredSubcategory),
                parseMode: ParseMode.Html,
                cancellationToken: ct);
            await bot.SendTextMessageAsync(
                chatId,
                $"📝 <b>{Html(quiz.Question)}</b>",
                parseMode: ParseMode.Html,
                cancellationToken: ct);
            return;
        }

        var options = quiz.GetOptions();
        TelegramDebugTrace.Write("student.quiz", "prepare:poll", ("chatId", chatId), ("userId", userId), ("quizId", quiz.Id), ("category", quiz.Category), ("subcategory", quiz.Subcategory), ("question", quiz.Question), ("options", string.Join(" | ", options)), ("correctOptionId", quiz.CorrectOptionId));
        if (options.Length == 0 || quiz.CorrectOptionId is null)
        {
            await bot.SendTextMessageAsync(chatId, $"⚠️ Вопрос {quiz.Id} повреждён: нет вариантов ответа.", replyMarkup: AfterAnswerKeyboard(false), cancellationToken: ct);
            return;
        }

        await bot.SendTextMessageAsync(
            chatId,
            BuildQuizTopicHeader(quiz, settings, next, preferredSubcategory),
            parseMode: ParseMode.Html,
            cancellationToken: ct);

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
        {
            _state.ActivePolls[pollId] = new ActivePollQuiz { UserId = userId, ChatId = chatId, Quiz = quiz };
            TelegramDebugTrace.Write("student.quiz", "send:poll:registered", ("chatId", chatId), ("userId", userId), ("quizId", quiz.Id), ("pollId", pollId), ("messageId", sent.MessageId));
        }
        else
        {
            TelegramDebugTrace.Write("student.quiz", "send:poll:no-poll-id", ("chatId", chatId), ("userId", userId), ("quizId", quiz.Id), ("messageId", sent.MessageId));
        }
    }

    private async Task HandlePollAnswerAsync(PollAnswer answer, CancellationToken ct)
    {
        TelegramDebugTrace.Write("student.poll", "answer:received", ("pollId", answer.PollId), ("userId", answer.User.Id), ("username", answer.User.Username), ("optionIds", string.Join(",", answer.OptionIds)));
        if (!_state.ActivePolls.TryRemove(answer.PollId, out var active))
        {
            TelegramDebugTrace.Write("student.poll", "answer:ignored:unknown-poll", ("pollId", answer.PollId), ("userId", answer.User.Id));
            return;
        }

        using var scope = _provider.CreateScope();
        var progress = scope.ServiceProvider.GetRequiredService<ProgressService>();
        var selected = answer.OptionIds.FirstOrDefault();
        var correct = active.Quiz.CorrectOptionId == selected;
        TelegramDebugTrace.Write("student.poll", "answer:checked", ("pollId", answer.PollId), ("userId", answer.User.Id), ("quizId", active.Quiz.Id), ("selected", selected), ("correctOptionId", active.Quiz.CorrectOptionId), ("correct", correct));
        await progress.SaveAnswerAsync(answer.User.Id, answer.User.Username, active.Quiz, correct, ct);

        if (_bot == null)
        {
            TelegramDebugTrace.Write("student.poll", "answer:no-bot-client", ("pollId", answer.PollId), ("quizId", active.Quiz.Id));
            return;
        }

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
        if (settings != null)
        {
            TelegramDebugTrace.Write("student.settings", "loaded", ("userId", userId), ("mode", settings.LearningMode), ("category", settings.SelectedCategory));
            return settings;
        }
        settings = new UserSetting { UserId = userId, LearningMode = ModeNormal, SelectedCategory = AllCategories };
        db.UserSettings.Add(settings);
        await db.SaveChangesAsync(ct);
        TelegramDebugTrace.Write("student.settings", "created", ("userId", userId), ("mode", settings.LearningMode), ("category", settings.SelectedCategory));
        return settings;
    }

    private static async Task LogStartAsync(TelegramQuizDbContext db, Message message, CancellationToken ct)
    {
        if (message.Text != "/start") return;
        TelegramDebugTrace.Write("student.start-log", "add", ("userId", message.From!.Id), ("username", message.From.Username));
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
        bool includeAllTopics,
        CancellationToken ct)
    {
        var p = await progress.GetProgressAsync(userId, ct);
        var topics = await quizzes.GetTopicProgressAsync(userId, includeAllTopics, 12, ct);
        var recommendations = await quizzes.GetSmartRecommendationsAsync(userId, 5, ct);
        TelegramDebugTrace.Write("student.stats", "show", ("chatId", chatId), ("userId", userId), ("total", p.Total), ("correct", p.Correct), ("incorrect", p.Incorrect), ("experience", p.Experience), ("includeAllTopics", includeAllTopics), ("topics", topics.Count), ("recommendations", recommendations.Count));

        var modeTitle = includeAllTopics ? "все темы" : "только темы, которые вы уже решали";
        var lines = new List<string>
        {
            "<b>📊 Ваша статистика</b>",
            $"Всего: <b>{p.Total}</b>",
            $"✅ Правильно: <b>{p.Correct}</b>",
            $"❌ Ошибок: <b>{p.Incorrect}</b>",
            $"⭐ Опыт: <b>{p.Experience}</b>",
            string.Empty,
            $"<b>📌 Темы: {Html(modeTitle)}</b>"
        };

        if (topics.Count == 0)
        {
            lines.Add("Пока нет решённых тем. Нажмите 🎯 Задание и ответьте на пару вопросов.");
        }
        else
        {
            foreach (var (item, index) in topics.Select((item, index) => (item, index + 1)))
            {
                lines.Add($"{index}. {FormatTopicProgress(item)}");
            }
        }

        lines.Add(string.Empty);
        lines.Add("<b>💡 Почему именно эти темы в умных рекомендациях:</b>");
        lines.Add("1) сначала беру темы, где были ошибки или низкая точность;");
        lines.Add("2) если таких мало — добавляю темы, которые ещё не решались;");
        lines.Add("3) темы со 100% точностью не считаю слабыми, но могу дать их для закрепления.");

        if (recommendations.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add("<b>🎯 Сейчас бот будет тренировать:</b>");
            foreach (var (item, index) in recommendations.Select((item, index) => (item, index + 1)))
            {
                lines.Add($"{index}. {FormatSmartRecommendation(item)}");
            }
        }

        await bot.SendTextMessageAsync(
            chatId,
            string.Join('\n', lines),
            parseMode: ParseMode.Html,
            replyMarkup: StatsKeyboard(includeAllTopics),
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
        TelegramDebugTrace.Write("student.ui", "category-picker", ("chatId", chatId), ("count", categories.Count), ("categories", string.Join(",", categories.Select(x => $"{x.Name}:{x.Count}"))));
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
        TelegramDebugTrace.Write("student.smart", "recommendations", ("chatId", chatId), ("userId", userId), ("count", recommendations.Count), ("items", string.Join(";", recommendations.Select(x => $"{x.Category}/{x.Subcategory}:{x.Accuracy:0.#}%:{x.Correct}/{x.Total}:{x.QuestionCount}"))));
        var lines = new List<string>
        {
            "💡 <b>Умные рекомендации включены</b>",
            "",
            "Я буду чаще предлагать вопросы по темам ниже.",
            "",
            "<b>Почему именно они:</b>",
            "• сначала идут темы с ошибками и низкой точностью;",
            "• если ошибок мало — добавляю темы без практики;",
            "• 0% больше не означает \"плохо\", если тема ещё не решалась."
        };

        if (recommendations.Count == 0)
        {
            lines.Add("");
            lines.Add("Пока статистики нет. Начнём с разных тем, а затем бот сам найдёт слабые места.");
        }
        else
        {
            lines.Add("");
            lines.Add("<b>Темы тренировки:</b>");
            foreach (var (item, index) in recommendations.Select((item, index) => (item, index + 1)))
            {
                lines.Add($"{index}. {FormatSmartRecommendation(item)}");
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

    private static InlineKeyboardMarkup StatsKeyboard(bool includeAllTopics)
    {
        return new InlineKeyboardMarkup(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData(includeAllTopics ? "✅ Только решённые" : "🗂 Все темы", includeAllTopics ? "st:stats:solved" : "st:stats:all"),
                InlineKeyboardButton.WithCallbackData("💡 Умные рекомендации", "st:mode:smart")
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData("🎯 Задание", "st:next"),
                InlineKeyboardButton.WithCallbackData("🏠 Меню", "st:menu")
            }
        });
    }

    private static string BuildQuizTopicHeader(QuizQuestion quiz, UserSetting settings, NextQuizResult next, string? preferredSubcategory)
    {
        var title = settings.SelectedCategory == AllCategories
            ? "🎲 Случайная категория"
            : "📌 Текущая тема";

        if (settings.LearningMode == ModeSmart)
            title = "💡 Умная рекомендация";

        var lines = new List<string>
        {
            $"{title}: <b>{Html(quiz.Category)}</b> / <b>{Html(quiz.Subcategory)}</b>",
            $"Вопросов в наборе: <b>{next.TotalAvailable}</b>; непройденных: <b>{next.UnseenAvailable}</b>."
        };

        if (!string.IsNullOrWhiteSpace(preferredSubcategory))
            lines.Add($"Приоритетная тема: <b>{Html(preferredSubcategory)}</b>.");

        if (next.FilterWasRelaxed)
            lines.Add("Фильтр расширен, потому что в выбранной теме не осталось новых вопросов.");

        if (next.IsRepeatCycle)
            lines.Add("Начался новый круг: все вопросы этого набора уже были решены.");

        return string.Join('\n', lines);
    }

    private static string FormatSmartRecommendation(SmartRecommendation item)
    {
        var name = $"{Html(item.Category)} / {Html(item.Subcategory)}";
        if (item.Total == 0)
            return $"{name} — ещё не решали, вопросов в базе: {item.QuestionCount}";

        var reason = item.Incorrect > 0
            ? $"ошибок: {item.Incorrect}"
            : "для закрепления";

        return $"{name} — точность {item.Accuracy:0.#}% ({item.Correct}/{item.Total}), {reason}, вопросов в базе: {item.QuestionCount}";
    }

    private static string FormatTopicProgress(TopicProgressItem item)
    {
        var name = $"{Html(item.Category)} / {Html(item.Subcategory)}";
        if (item.Total == 0)
            return $"{name} — ещё не решали, вопросов в базе: {item.QuestionCount}";

        return $"{name} — точность {item.Accuracy:0.#}% ({item.Correct}/{item.Total}), ошибок: {item.Incorrect}, вопросов в базе: {item.QuestionCount}";
    }

    private Task HandleErrorAsync(ITelegramBotClient bot, Exception exception, CancellationToken ct)
    {
        if (TelegramPollingErrorClassifier.IsExpectedLongPollingTimeout(exception))
        {
            TelegramDebugTrace.Exception("student.error", "long-polling-timeout", exception);
            _logger.LogDebug("Student bot long polling timeout");
            return Task.CompletedTask;
        }

        if (TelegramPollingErrorClassifier.IsTransientTelegramApiError(exception))
        {
            TelegramDebugTrace.Exception("student.error", "transient", exception);
            _logger.LogWarning("Student bot transient Telegram polling error: {Message}", exception.Message);
            return Task.Delay(TimeSpan.FromSeconds(5), ct);
        }

        TelegramDebugTrace.Exception("student.error", "fatal", exception);
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
