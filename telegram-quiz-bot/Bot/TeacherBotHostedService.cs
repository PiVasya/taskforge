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

public sealed class TeacherBotHostedService : BackgroundService
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

    private async Task HandleMessageAsync(ITelegramBotClient bot, Message message, CancellationToken ct)
    {
        var teacherId = message.From?.Id ?? 0;
        TelegramDebugTrace.Write(
            "teacher.message",
            "received",
            ("messageId", message.MessageId),
            ("chatId", message.Chat.Id),
            ("teacherId", teacherId),
            ("username", message.From?.Username),
            ("firstName", message.From?.FirstName),
            ("lastName", message.From?.LastName),
            ("text", message.Text),
            ("type", message.Type),
            ("hasPhoto", message.Photo is { Length: > 0 }),
            ("hasPoll", message.Poll is not null));

        if (teacherId == 0)
        {
            TelegramDebugTrace.Write("teacher.message", "ignored:no-user");
            return;
        }

        using var scope = _provider.CreateScope();
        var teachers = scope.ServiceProvider.GetRequiredService<TeacherAccessService>();
        var students = scope.ServiceProvider.GetRequiredService<StudentAccessService>();
        var directory = scope.ServiceProvider.GetRequiredService<StudentDirectoryService>();
        var categories = scope.ServiceProvider.GetRequiredService<CategoryService>();
        var quizzes = scope.ServiceProvider.GetRequiredService<QuizService>();
        var breaks = scope.ServiceProvider.GetRequiredService<TechnicalBreakService>();
        var stats = scope.ServiceProvider.GetRequiredService<StatisticsService>();
        var imageStorage = scope.ServiceProvider.GetRequiredService<IS3ImageStorage>();

        var text = message.Text?.Trim();

        if (text == "/start")
        {
            TelegramDebugTrace.Write("teacher.message", "command:start", ("teacherId", teacherId));
            if (await teachers.IsTeacherAsync(teacherId, ct))
                await SendTeacherHomeAsync(bot, message.Chat.Id, ct);
            else
                await bot.SendTextMessageAsync(message.Chat.Id, "🔐 Для доступа введите пароль учителя:", cancellationToken: ct);
            return;
        }

        if (!string.IsNullOrWhiteSpace(_options.TeacherPassword) && text == _options.TeacherPassword)
        {
            TelegramDebugTrace.Write("teacher.message", "password:accepted", ("teacherId", teacherId));
            await teachers.AuthorizeAsync(teacherId, ct);
            await bot.SendTextMessageAsync(message.Chat.Id, "🔓 Авторизация успешна. Помощников может быть сколько угодно — каждый входит по паролю.", replyMarkup: TeacherMainKeyboard(), cancellationToken: ct);
            await SendTeacherHomeAsync(bot, message.Chat.Id, ct);
            return;
        }

        var isTeacher = await teachers.IsTeacherAsync(teacherId, ct);
        TelegramDebugTrace.Write("teacher.message", "access-check", ("teacherId", teacherId), ("isTeacher", isTeacher), ("text", text));
        if (!isTeacher)
        {
            TelegramDebugTrace.Write("teacher.message", "blocked:not-teacher", ("teacherId", teacherId), ("text", text));
            await bot.SendTextMessageAsync(message.Chat.Id, "⛔ Доступ запрещён. Введите пароль учителя.", cancellationToken: ct);
            return;
        }

        if (message.Photo is { Length: > 0 } && _state.Drafts.TryGetValue(teacherId, out var photoDraft) && photoDraft.Step == "image")
        {
            TelegramDebugTrace.Write("teacher.draft", "image:received", ("teacherId", teacherId), ("chatId", message.Chat.Id), ("photoCount", message.Photo.Length), ("draftType", photoDraft.Type), ("step", photoDraft.Step));
            var fileId = message.Photo.OrderByDescending(x => x.FileSize ?? 0).First().FileId;
            var file = await bot.GetFileAsync(fileId, ct);
            await using var stream = new MemoryStream();
            await bot.DownloadFileAsync(file.FilePath!, stream, ct);
            stream.Position = 0;
            photoDraft.ImageKey = await imageStorage.SaveImageAsync(stream, "image/jpeg", ".jpg", ct);
            TelegramDebugTrace.Write("teacher.draft", "image:saved", ("teacherId", teacherId), ("imageKey", photoDraft.ImageKey));
            photoDraft.Step = "question";
            await bot.SendTextMessageAsync(message.Chat.Id, "✅ Изображение сохранено в MinIO. Теперь введите текст вопроса:", cancellationToken: ct);
            return;
        }

        if (message.Poll is { } poll)
        {
            TelegramDebugTrace.Write("teacher.quiz", "poll:received", ("teacherId", teacherId), ("pollId", poll.Id), ("question", poll.Question), ("type", poll.Type), ("correctOptionId", poll.CorrectOptionId));
            await SavePollQuizAsync(bot, message, poll, quizzes, ct);
            return;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            TelegramDebugTrace.Write("teacher.message", "ignored:empty-text", ("teacherId", teacherId));
            return;
        }

        if (_state.Drafts.TryGetValue(teacherId, out var draft))
        {
            TelegramDebugTrace.Write("teacher.draft", "continue", ("teacherId", teacherId), ("type", draft.Type), ("step", draft.Step), ("text", text));
            await ContinueDraftAsync(bot, message, quizzes, draft, ct);
            return;
        }

        if (await HandleTeacherButtonAsync(bot, message.Chat.Id, text, quizzes, directory, stats, ct))
        {
            TelegramDebugTrace.Write("teacher.message", "handled:reply-keyboard", ("teacherId", teacherId), ("text", text));
            return;
        }

        TelegramDebugTrace.Write("teacher.message", "command-dispatch", ("teacherId", teacherId), ("text", text));

        if (text.StartsWith("/help"))
        {
            await bot.SendTextMessageAsync(message.Chat.Id, TelegramText.TeacherHelp, replyMarkup: TeacherMainKeyboard(), cancellationToken: ct);
        }
        else if (text.StartsWith("/logout"))
        {
            await teachers.LogoutAsync(teacherId, ct);
            await bot.SendTextMessageAsync(message.Chat.Id, "✅ Вы вышли из аккаунта учителя.", cancellationToken: ct);
        }
        else if (text.StartsWith("/students") || text.StartsWith("/find_student") || text.StartsWith("/search_student"))
        {
            var query = StripCommand(text);
            await SendStudentListAsync(bot, message.Chat.Id, directory, query, ct);
        }
        else if (text.StartsWith("/student "))
        {
            if (!TryReadLongArgument(text, out var studentId))
            {
                await bot.SendTextMessageAsync(message.Chat.Id, "❌ Формат: /student user_id", cancellationToken: ct);
                return;
            }
            await SendStudentCardAsync(bot, message.Chat.Id, directory, studentId, ct);
        }
        else if (text.StartsWith("/student_clean"))
        {
            await HandleStudentCleanAsync(bot, message, text, directory, ct);
        }
        else if (text.StartsWith("/grant_user") || text.StartsWith("/add_user"))
        {
            var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !long.TryParse(parts[1], out var studentId))
            {
                await bot.SendTextMessageAsync(message.Chat.Id, "❌ Формат: /grant_user user_id [hours]", cancellationToken: ct);
                return;
            }
            int? hours = null;
            if (parts.Length > 2 && int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedHours)) hours = parsedHours;
            await students.AddAsync(studentId, hours, ct);
            await bot.SendTextMessageAsync(message.Chat.Id, hours is > 0 ? $"✅ Ученик {studentId} получил доступ на {hours} часов." : $"✅ Ученик {studentId} получил постоянный доступ.", cancellationToken: ct);
        }
        else if (text.StartsWith("/revoke_user") || text.StartsWith("/remove_user"))
        {
            if (!TryReadLongArgument(text, out var studentId))
            {
                await bot.SendTextMessageAsync(message.Chat.Id, "❌ Формат: /revoke_user user_id", cancellationToken: ct);
                return;
            }
            var removed = await students.RemoveAsync(studentId, ct);
            await bot.SendTextMessageAsync(message.Chat.Id, removed ? $"✅ Доступ ученика {studentId} отозван." : $"❌ Ученик {studentId} не найден в whitelist.", cancellationToken: ct);
        }
        else if (text.StartsWith("/add_category"))
        {
            var name = text["/add_category".Length..].Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                await bot.SendTextMessageAsync(message.Chat.Id, "❌ Формат: /add_category Название", cancellationToken: ct);
                return;
            }
            await categories.AddCategoryAsync(name, ct);
            await bot.SendTextMessageAsync(message.Chat.Id, $"✅ Категория добавлена: {name}", cancellationToken: ct);
        }
        else if (text.StartsWith("/remove_category"))
        {
            var name = text["/remove_category".Length..].Trim();
            var removed = await categories.RemoveCategoryAsync(name, ct);
            await bot.SendTextMessageAsync(message.Chat.Id, removed ? $"✅ Категория удалена: {name}" : "❌ Категория не найдена.", cancellationToken: ct);
        }
        else if (text.StartsWith("/list_categories"))
        {
            var list = await categories.GetCategoriesAsync(ct);
            await bot.SendTextMessageAsync(message.Chat.Id, "📁 Категории:\n" + string.Join('\n', list), cancellationToken: ct);
        }
        else if (text.StartsWith("/add_quiz"))
        {
            await bot.SendTextMessageAsync(message.Chat.Id, "📝 Отправьте Telegram-опрос типа quiz. Он сохранится в категорию 'Остальное'.", cancellationToken: ct);
        }
        else if (text.StartsWith("/add_text_question"))
        {
            _state.Drafts[teacherId] = new TeacherDraftQuestion { Type = "text", Step = "image" };
            await bot.SendTextMessageAsync(message.Chat.Id, "📸 Отправьте изображение для вопроса или напишите /skip.", cancellationToken: ct);
        }
        else if (text.StartsWith("/skip"))
        {
            _state.Drafts[teacherId] = new TeacherDraftQuestion { Type = "text", Step = "question" };
            await bot.SendTextMessageAsync(message.Chat.Id, "📝 Введите текст вопроса:", cancellationToken: ct);
        }
        else if (text.StartsWith("/remove_quiz"))
        {
            if (!TryReadLongArgument(text, out var id))
            {
                await bot.SendTextMessageAsync(message.Chat.Id, "❌ Формат: /remove_quiz id", cancellationToken: ct);
                return;
            }
            var removed = await quizzes.RemoveAsync(id, ct);
            await bot.SendTextMessageAsync(message.Chat.Id, removed ? $"✅ Вопрос {id} удалён." : $"❌ Вопрос {id} не найден.", cancellationToken: ct);
        }
        else if (text.StartsWith("/list_quizzes"))
        {
            await SendQuizListAsync(bot, message.Chat.Id, null, quizzes, 1, 0, 0, ct);
        }
        else if (text.StartsWith("/class_stats"))
        {
            await bot.SendTextMessageAsync(message.Chat.Id, await stats.BuildClassStatsAsync(ct), cancellationToken: ct);
        }
        else if (text.StartsWith("/log_start"))
        {
            await bot.SendTextMessageAsync(message.Chat.Id, await stats.BuildStartLogAsync(ct), cancellationToken: ct);
        }
        else if (text.StartsWith("/technical_break"))
        {
            await HandleTechnicalBreakAsync(bot, message, text, breaks, ct);
        }
        else
        {
            await bot.SendTextMessageAsync(message.Chat.Id, "Не понял. Пользуйся кнопками ниже 👇", replyMarkup: TeacherMainKeyboard(), cancellationToken: ct);
        }
    }

    private async Task HandleCallbackAsync(ITelegramBotClient bot, CallbackQuery callback, CancellationToken ct)
    {
        var teacherId = callback.From.Id;
        var chatId = callback.Message?.Chat.Id ?? teacherId;
        var messageId = callback.Message?.MessageId;
        var data = callback.Data ?? string.Empty;
        TelegramDebugTrace.Write(
            "teacher.callback",
            "received",
            ("callbackId", callback.Id),
            ("chatId", chatId),
            ("teacherId", teacherId),
            ("username", callback.From.Username),
            ("messageId", messageId),
            ("data", data));

        using var scope = _provider.CreateScope();
        var teachers = scope.ServiceProvider.GetRequiredService<TeacherAccessService>();
        var isTeacher = await teachers.IsTeacherAsync(teacherId, ct);
        TelegramDebugTrace.Write("teacher.callback", "access-check", ("teacherId", teacherId), ("isTeacher", isTeacher), ("data", data));
        if (!isTeacher)
        {
            TelegramDebugTrace.Write("teacher.callback", "blocked:not-teacher", ("teacherId", teacherId), ("data", data));
            await bot.SafeAnswerCallbackQueryAsync(callback.Id, _logger, "Доступ запрещён", showAlert: true, cancellationToken: ct);
            return;
        }

        try
        {
            if (data.StartsWith("tq:", StringComparison.Ordinal))
            {
                TelegramDebugTrace.Write("teacher.callback", "dispatch:quiz", ("teacherId", teacherId), ("data", data));
                var quizzes = scope.ServiceProvider.GetRequiredService<QuizService>();
                await HandleQuizCallbackAsync(bot, callback, chatId, messageId, data, quizzes, ct);
                return;
            }

            if (data.StartsWith("tm:", StringComparison.Ordinal))
            {
                TelegramDebugTrace.Write("teacher.callback", "dispatch:menu", ("teacherId", teacherId), ("data", data));
                var quizzes = scope.ServiceProvider.GetRequiredService<QuizService>();
                var directory = scope.ServiceProvider.GetRequiredService<StudentDirectoryService>();
                var stats = scope.ServiceProvider.GetRequiredService<StatisticsService>();
                await HandleTeacherMenuCallbackAsync(bot, callback, chatId, messageId, teacherId, data, quizzes, directory, stats, ct);
                return;
            }

            if (data.StartsWith("sq:", StringComparison.Ordinal))
            {
                TelegramDebugTrace.Write("teacher.callback", "dispatch:student", ("teacherId", teacherId), ("data", data));
                var students = scope.ServiceProvider.GetRequiredService<StudentAccessService>();
                var directory = scope.ServiceProvider.GetRequiredService<StudentDirectoryService>();
                await HandleStudentCallbackAsync(bot, callback, chatId, data, teacherId, students, directory, ct);
                return;
            }

            TelegramDebugTrace.Write("teacher.callback", "unknown-data", ("teacherId", teacherId), ("data", data));
            await bot.SafeAnswerCallbackQueryAsync(callback.Id, _logger, cancellationToken: ct);
        }
        catch (Exception ex)
        {
            TelegramDebugTrace.Exception("teacher.callback", "error", ex, ("teacherId", teacherId), ("data", data));
            _logger.LogError(ex, "Teacher callback handling failed");
            await bot.SafeAnswerCallbackQueryAsync(callback.Id, _logger, "Ошибка обработки кнопки", showAlert: true, cancellationToken: ct);
        }
    }

    private async Task HandleTeacherMenuCallbackAsync(
        ITelegramBotClient bot,
        CallbackQuery callback,
        long chatId,
        int? messageId,
        long teacherId,
        string data,
        QuizService quizzes,
        StudentDirectoryService directory,
        StatisticsService stats,
        CancellationToken ct)
    {
        switch (data)
        {
            case "tm:home":
                TelegramDebugTrace.Write("teacher.callback", "ack", ("data", data));
            await bot.SafeAnswerCallbackQueryAsync(callback.Id, _logger, cancellationToken: ct);
                await SendTeacherHomeAsync(bot, chatId, ct);
                break;
            case "tm:quizzes":
                TelegramDebugTrace.Write("teacher.callback", "ack", ("data", data));
            await bot.SafeAnswerCallbackQueryAsync(callback.Id, _logger, cancellationToken: ct);
                await SendQuizListAsync(bot, chatId, messageId, quizzes, 1, 0, 0, ct);
                break;
            case "tm:students":
                TelegramDebugTrace.Write("teacher.callback", "ack", ("data", data));
            await bot.SafeAnswerCallbackQueryAsync(callback.Id, _logger, cancellationToken: ct);
                await SendStudentListAsync(bot, chatId, directory, null, ct);
                break;
            case "tm:addText":
                _state.Drafts[teacherId] = new TeacherDraftQuestion { Type = "text", Step = "image" };
                TelegramDebugTrace.Write("teacher.callback", "ack", ("data", data));
            await bot.SafeAnswerCallbackQueryAsync(callback.Id, _logger, cancellationToken: ct);
                await bot.SendTextMessageAsync(chatId, "📸 Отправьте изображение для вопроса или напишите /skip.", replyMarkup: TeacherMainKeyboard(), cancellationToken: ct);
                break;
            case "tm:addQuizInfo":
                TelegramDebugTrace.Write("teacher.callback", "ack", ("data", data));
            await bot.SafeAnswerCallbackQueryAsync(callback.Id, _logger, cancellationToken: ct);
                await bot.SendTextMessageAsync(chatId, "📊 Чтобы добавить quiz-вопрос, отправь сюда Telegram-опрос типа <b>quiz</b> с выбранным правильным ответом. Бот сохранит его автоматически.", parseMode: ParseMode.Html, replyMarkup: TeacherMainKeyboard(), cancellationToken: ct);
                break;
            case "tm:stats":
                TelegramDebugTrace.Write("teacher.callback", "ack", ("data", data));
            await bot.SafeAnswerCallbackQueryAsync(callback.Id, _logger, cancellationToken: ct);
                await bot.SendTextMessageAsync(chatId, await stats.BuildClassStatsAsync(ct), replyMarkup: TeacherMainKeyboard(), cancellationToken: ct);
                break;
            case "tm:logs":
                TelegramDebugTrace.Write("teacher.callback", "ack", ("data", data));
            await bot.SafeAnswerCallbackQueryAsync(callback.Id, _logger, cancellationToken: ct);
                await bot.SendTextMessageAsync(chatId, await stats.BuildStartLogAsync(ct), replyMarkup: TeacherMainKeyboard(), cancellationToken: ct);
                break;
            case "tm:clean":
                TelegramDebugTrace.Write("teacher.callback", "ack", ("data", data));
            await bot.SafeAnswerCallbackQueryAsync(callback.Id, _logger, cancellationToken: ct);
                await bot.SendTextMessageAsync(chatId, TelegramText.StudentCleanHelp, replyMarkup: TeacherMainKeyboard(), cancellationToken: ct);
                break;
            default:
                TelegramDebugTrace.Write("teacher.callback", "ack", ("data", data));
            await bot.SafeAnswerCallbackQueryAsync(callback.Id, _logger, cancellationToken: ct);
                break;
        }
    }

    private async Task HandleStudentCallbackAsync(
        ITelegramBotClient bot,
        CallbackQuery callback,
        long chatId,
        string data,
        long teacherId,
        StudentAccessService students,
        StudentDirectoryService directory,
        CancellationToken ct)
    {
        var parts = data.Split(':', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3 || parts[0] != "sq")
        {
            TelegramDebugTrace.Write("teacher.callback", "ack", ("data", data));
            await bot.SafeAnswerCallbackQueryAsync(callback.Id, _logger, cancellationToken: ct);
            return;
        }

        var action = parts[1];
        TelegramDebugTrace.Write("teacher.student-callback", "parsed", ("teacherId", teacherId), ("action", action), ("raw", data));
        if (!long.TryParse(parts[2], out var studentId))
        {
            await bot.SafeAnswerCallbackQueryAsync(callback.Id, _logger, "Некорректный ID", showAlert: true, cancellationToken: ct);
            return;
        }

        switch (action)
        {
            case "card":
                await SendStudentCardAsync(bot, chatId, directory, studentId, ct);
                break;
            case "grant24":
                await students.AddAsync(studentId, 24, ct);
                await bot.SafeAnswerCallbackQueryAsync(callback.Id, _logger, "Доступ на 24 часа выдан", cancellationToken: ct);
                await SendStudentCardAsync(bot, chatId, directory, studentId, ct);
                break;
            case "grant7":
                await students.AddAsync(studentId, 24 * 7, ct);
                await bot.SafeAnswerCallbackQueryAsync(callback.Id, _logger, "Доступ на 7 дней выдан", cancellationToken: ct);
                await SendStudentCardAsync(bot, chatId, directory, studentId, ct);
                break;
            case "grantForever":
                await students.AddAsync(studentId, null, ct);
                await bot.SafeAnswerCallbackQueryAsync(callback.Id, _logger, "Постоянный доступ выдан", cancellationToken: ct);
                await SendStudentCardAsync(bot, chatId, directory, studentId, ct);
                break;
            case "revoke":
                await students.RemoveAsync(studentId, ct);
                await bot.SafeAnswerCallbackQueryAsync(callback.Id, _logger, "Доступ отозван", cancellationToken: ct);
                await SendStudentCardAsync(bot, chatId, directory, studentId, ct);
                break;
            case "hide":
                await directory.HideContactAsync(studentId, teacherId, ct);
                await bot.SafeAnswerCallbackQueryAsync(callback.Id, _logger, "Контакт скрыт из общего списка", cancellationToken: ct);
                break;
            case "delete":
                await directory.DeleteContactAsync(studentId, deleteAccess: false, deleteProgress: false, deleteLogs: false, ct);
                await bot.SafeAnswerCallbackQueryAsync(callback.Id, _logger, "Контакт удалён из справочника", cancellationToken: ct);
                break;
            default:
                TelegramDebugTrace.Write("teacher.callback", "ack", ("data", data));
            await bot.SafeAnswerCallbackQueryAsync(callback.Id, _logger, cancellationToken: ct);
                break;
        }
    }

    private async Task HandleQuizCallbackAsync(
        ITelegramBotClient bot,
        CallbackQuery callback,
        long chatId,
        int? messageId,
        string data,
        QuizService quizzes,
        CancellationToken ct)
    {
        var parts = data.Split(':', StringSplitOptions.RemoveEmptyEntries);
        var action = parts.Length > 1 ? parts[1] : string.Empty;
        TelegramDebugTrace.Write("teacher.quiz-callback", "parsed", ("action", action), ("raw", data), ("chatId", chatId), ("messageId", messageId));

        switch (action)
        {
            case "list":
            {
                var page = ReadInt(parts, 2, 1);
                var categoryIndex = ReadInt(parts, 3, 0);
                var subcategoryIndex = ReadInt(parts, 4, 0);
                TelegramDebugTrace.Write("teacher.callback", "ack", ("data", data));
            await bot.SafeAnswerCallbackQueryAsync(callback.Id, _logger, cancellationToken: ct);
                await SendQuizListAsync(bot, chatId, messageId, quizzes, page, categoryIndex, subcategoryIndex, ct);
                break;
            }
            case "cats":
            {
                var page = ReadInt(parts, 2, 1);
                TelegramDebugTrace.Write("teacher.callback", "ack", ("data", data));
            await bot.SafeAnswerCallbackQueryAsync(callback.Id, _logger, cancellationToken: ct);
                await SendQuizCategoryPickerAsync(bot, chatId, messageId, quizzes, page, ct);
                break;
            }
            case "setcat":
            {
                var categoryIndex = ReadInt(parts, 2, 0);
                TelegramDebugTrace.Write("teacher.callback", "ack", ("data", data));
            await bot.SafeAnswerCallbackQueryAsync(callback.Id, _logger, cancellationToken: ct);
                await SendQuizListAsync(bot, chatId, messageId, quizzes, 1, categoryIndex, 0, ct);
                break;
            }
            case "subs":
            {
                var categoryIndex = ReadInt(parts, 2, 0);
                var page = ReadInt(parts, 3, 1);
                TelegramDebugTrace.Write("teacher.callback", "ack", ("data", data));
            await bot.SafeAnswerCallbackQueryAsync(callback.Id, _logger, cancellationToken: ct);
                await SendQuizSubcategoryPickerAsync(bot, chatId, messageId, quizzes, categoryIndex, page, ct);
                break;
            }
            case "setsub":
            {
                var categoryIndex = ReadInt(parts, 2, 0);
                var subcategoryIndex = ReadInt(parts, 3, 0);
                TelegramDebugTrace.Write("teacher.callback", "ack", ("data", data));
            await bot.SafeAnswerCallbackQueryAsync(callback.Id, _logger, cancellationToken: ct);
                await SendQuizListAsync(bot, chatId, messageId, quizzes, 1, categoryIndex, subcategoryIndex, ct);
                break;
            }
            case "card":
            {
                var quizId = ReadLong(parts, 2, 0);
                var page = ReadInt(parts, 3, 1);
                var categoryIndex = ReadInt(parts, 4, 0);
                var subcategoryIndex = ReadInt(parts, 5, 0);
                TelegramDebugTrace.Write("teacher.callback", "ack", ("data", data));
            await bot.SafeAnswerCallbackQueryAsync(callback.Id, _logger, cancellationToken: ct);
                await SendQuizCardAsync(bot, chatId, messageId, quizzes, quizId, page, categoryIndex, subcategoryIndex, ct);
                break;
            }
            case "del":
            {
                var quizId = ReadLong(parts, 2, 0);
                var page = ReadInt(parts, 3, 1);
                var categoryIndex = ReadInt(parts, 4, 0);
                var subcategoryIndex = ReadInt(parts, 5, 0);
                var removed = await quizzes.RemoveAsync(quizId, ct);
                await bot.SafeAnswerCallbackQueryAsync(callback.Id, _logger, removed ? "Вопрос удалён" : "Вопрос не найден", cancellationToken: ct);
                await SendQuizListAsync(bot, chatId, messageId, quizzes, page, categoryIndex, subcategoryIndex, ct);
                break;
            }
            default:
                TelegramDebugTrace.Write("teacher.callback", "ack", ("data", data));
            await bot.SafeAnswerCallbackQueryAsync(callback.Id, _logger, cancellationToken: ct);
                break;
        }
    }

    private static async Task SavePollQuizAsync(ITelegramBotClient bot, Message message, Poll poll, QuizService quizzes, CancellationToken ct)
    {
        TelegramDebugTrace.Write("teacher.quiz", "save-poll:start", ("chatId", message.Chat.Id), ("pollId", poll.Id), ("question", poll.Question), ("type", poll.Type), ("correctOptionId", poll.CorrectOptionId), ("options", string.Join(" | ", poll.Options.Select(x => x.Text))));
        if (!string.Equals(poll.Type, "quiz", StringComparison.OrdinalIgnoreCase) || poll.CorrectOptionId is null)
        {
            TelegramDebugTrace.Write("teacher.quiz", "save-poll:rejected", ("pollId", poll.Id), ("type", poll.Type), ("correctOptionId", poll.CorrectOptionId));
            await bot.SendTextMessageAsync(message.Chat.Id, "❌ Нужен Telegram-опрос типа quiz с правильным ответом.", cancellationToken: ct);
            return;
        }

        var saved = await quizzes.AddPollQuizAsync(
            poll.Question,
            poll.Options.Select(x => x.Text).ToList(),
            poll.CorrectOptionId.Value,
            poll.Explanation,
            CategoryService.DefaultCategory,
            CategoryService.DefaultSubcategory,
            null,
            ct);

        TelegramDebugTrace.Write("teacher.quiz", "save-poll:done", ("quizId", saved.Id), ("category", saved.Category), ("subcategory", saved.Subcategory));
        await bot.SendTextMessageAsync(message.Chat.Id, $"✅ Quiz-вопрос сохранён. ID: {saved.Id}", cancellationToken: ct);
    }

    private async Task ContinueDraftAsync(ITelegramBotClient bot, Message message, QuizService quizzes, TeacherDraftQuestion draft, CancellationToken ct)
    {
        var teacherId = message.From!.Id;
        var text = message.Text?.Trim() ?? string.Empty;

        switch (draft.Step)
        {
            case "image":
                if (text == "/skip")
                {
                    draft.Step = "question";
                    await bot.SendTextMessageAsync(message.Chat.Id, "📝 Введите текст вопроса:", cancellationToken: ct);
                }
                break;
            case "question":
                draft.Question = text;
                draft.Step = "answer";
                await bot.SendTextMessageAsync(message.Chat.Id, "✅ Теперь введите правильный ответ:", cancellationToken: ct);
                break;
            case "answer":
                draft.Answer = text;
                draft.Step = "explanation";
                await bot.SendTextMessageAsync(message.Chat.Id, "💬 Введите пояснение или '-' если пояснение не нужно:", cancellationToken: ct);
                break;
            case "explanation":
                draft.Explanation = text == "-" ? string.Empty : text;
                draft.Step = "category";
                await bot.SendTextMessageAsync(message.Chat.Id, "📁 Введите категорию или '-' для 'Остальное':", cancellationToken: ct);
                break;
            case "category":
                draft.Category = text == "-" ? CategoryService.DefaultCategory : text;
                draft.Step = "subcategory";
                await bot.SendTextMessageAsync(message.Chat.Id, "📂 Введите подкатегорию или '-' для 'Без подкатегории':", cancellationToken: ct);
                break;
            case "subcategory":
                draft.Subcategory = text == "-" ? CategoryService.DefaultSubcategory : text;
                var saved = await quizzes.AddTextQuestionAsync(draft.Question!, draft.Answer!, draft.Explanation, draft.Category, draft.Subcategory, draft.ImageKey, ct);
                _state.Drafts.TryRemove(teacherId, out _);
                await bot.SendTextMessageAsync(message.Chat.Id, $"✅ Текстовый вопрос сохранён. ID: {saved.Id}", cancellationToken: ct);
                break;
        }
    }

    private static async Task<bool> HandleTeacherButtonAsync(
        ITelegramBotClient bot,
        long chatId,
        string text,
        QuizService quizzes,
        StudentDirectoryService directory,
        StatisticsService stats,
        CancellationToken ct)
    {
        switch (text)
        {
            case "🏠 Меню":
                await SendTeacherHomeAsync(bot, chatId, ct);
                return true;
            case "📚 Квизы":
                await SendQuizListAsync(bot, chatId, null, quizzes, 1, 0, 0, ct);
                return true;
            case "👥 Ученики":
                await SendStudentListAsync(bot, chatId, directory, null, ct);
                return true;
            case "➕ Вопрос":
                await bot.SendTextMessageAsync(
                    chatId,
                    "<b>➕ Добавление вопроса</b>\n\nВыбери тип добавления ниже.",
                    parseMode: ParseMode.Html,
                    replyMarkup: new InlineKeyboardMarkup(new[]
                    {
                        new[] { InlineKeyboardButton.WithCallbackData("📝 Текстовый вопрос", "tm:addText") },
                        new[] { InlineKeyboardButton.WithCallbackData("📊 Telegram quiz-опрос", "tm:addQuizInfo") },
                        new[] { InlineKeyboardButton.WithCallbackData("🏠 В меню", "tm:home") }
                    }),
                    cancellationToken: ct);
                return true;
            case "📊 Статистика":
                await bot.SendTextMessageAsync(chatId, await stats.BuildClassStatsAsync(ct), replyMarkup: TeacherMainKeyboard(), cancellationToken: ct);
                return true;
            case "🧹 Очистка":
                await bot.SendTextMessageAsync(chatId, TelegramText.StudentCleanHelp, replyMarkup: TeacherMainKeyboard(), cancellationToken: ct);
                return true;
            case "❓ Помощь":
                await bot.SendTextMessageAsync(chatId, TelegramText.TeacherHelp, replyMarkup: TeacherMainKeyboard(), cancellationToken: ct);
                return true;
            default:
                return false;
        }
    }

    private static async Task SendTeacherHomeAsync(ITelegramBotClient bot, long chatId, CancellationToken ct)
    {
        var text = string.Join('\n',
            "<b>🏠 Панель учителя</b>",
            "",
            "Теперь основной сценарий работает через кнопки.",
            "Команды оставлены как быстрые шорткаты, но пользоваться ими не обязательно.",
            "",
            "<b>Главное:</b>",
            "📚 Квизы — список, страницы, категории, карточки вопросов.",
            "👥 Ученики — кто писал student-боту и кому выдать доступ.",
            "➕ Вопрос — добавление нового задания.",
            "📊 Статистика — прогресс класса.");

        await bot.SendTextMessageAsync(
            chatId,
            text,
            parseMode: ParseMode.Html,
            replyMarkup: TeacherMainKeyboard(),
            cancellationToken: ct);
    }

    private static ReplyKeyboardMarkup TeacherMainKeyboard()
    {
        return new ReplyKeyboardMarkup(new[]
        {
            new[] { new KeyboardButton("📚 Квизы"), new KeyboardButton("👥 Ученики") },
            new[] { new KeyboardButton("➕ Вопрос"), new KeyboardButton("📊 Статистика") },
            new[] { new KeyboardButton("🧹 Очистка"), new KeyboardButton("❓ Помощь") },
            new[] { new KeyboardButton("🏠 Меню") }
        })
        {
            ResizeKeyboard = true,
            OneTimeKeyboard = false
        };
    }

    private const int QuizPageSize = 20;
    private const int PickerPageSize = 8;

    private static async Task SendQuizListAsync(
        ITelegramBotClient bot,
        long chatId,
        int? messageId,
        QuizService quizzes,
        int page,
        int categoryIndex,
        int subcategoryIndex,
        CancellationToken ct)
    {
        var category = await ResolveCategoryAsync(quizzes, categoryIndex, ct);
        var subcategory = categoryIndex == 0 ? null : await ResolveSubcategoryAsync(quizzes, category, subcategoryIndex, ct);
        var total = await quizzes.CountAsync(category, subcategory, ct);
        var pages = Math.Max(1, (int)Math.Ceiling(total / (double)QuizPageSize));
        page = Math.Clamp(page, 1, pages);
        var list = await quizzes.ListAsync(category, subcategory, (page - 1) * QuizPageSize, QuizPageSize, ct);
        var categories = await quizzes.GetCategoryStatsAsync(ct);
        var withImages = await quizzes.CountWithImagesAsync(ct);
        var missingAnswer = await quizzes.CountMissingAnswerAsync(ct);

        var text = BuildQuizListText(list, page, pages, total, category, subcategory, categories, withImages, missingAnswer);
        var keyboard = BuildQuizListKeyboard(list, page, pages, categoryIndex, subcategoryIndex);
        await SendOrEditTextAsync(bot, chatId, messageId, text, keyboard, ct);
    }

    private static string BuildQuizListText(
        IReadOnlyList<TelegramQuizBot.Data.Entities.QuizQuestion> list,
        int page,
        int pages,
        int total,
        string? category,
        string? subcategory,
        IReadOnlyList<QuizCategoryCount> categories,
        int withImages,
        int missingAnswer)
    {
        var rows = new List<string>
        {
            "<b>📚 Квизы</b>",
            $"Всего по фильтру: <b>{total}</b>. Страница <b>{page}/{pages}</b>.",
            $"Всего в базе: <b>{categories.Sum(x => x.Count)}</b>. Категорий: <b>{categories.Count}</b>. С картинками: <b>{withImages}</b>. Без ответа: <b>{missingAnswer}</b>.",
            $"Фильтр: <b>{Html(category ?? "Все категории")}</b> / <b>{Html(subcategory ?? "Все подкатегории")}</b>",
            "",
            "<pre>ID    Раздел          Тип   Вопрос"
        };

        foreach (var q in list)
        {
            var group = Trim($"{q.Category}/{q.Subcategory}", 14);
            rows.Add(string.Format(CultureInfo.InvariantCulture,
                "{0,4}  {1,-14} {2,-5} {3}",
                q.Id,
                Html(group),
                Html(q.Type),
                Html(Trim(q.Question.Replace('\n', ' '), 42))));
        }

        rows.Add("</pre>");
        rows.Add("Нажми ID ниже, чтобы открыть карточку вопроса.");
        return string.Join('\n', rows);
    }

    private static InlineKeyboardMarkup BuildQuizListKeyboard(
        IReadOnlyList<TelegramQuizBot.Data.Entities.QuizQuestion> list,
        int page,
        int pages,
        int categoryIndex,
        int subcategoryIndex)
    {
        var rows = new List<InlineKeyboardButton[]>();

        foreach (var chunk in list.Chunk(4))
        {
            rows.Add(chunk
                .Select(q => InlineKeyboardButton.WithCallbackData(q.Id.ToString(CultureInfo.InvariantCulture), $"tq:card:{q.Id}:{page}:{categoryIndex}:{subcategoryIndex}"))
                .ToArray());
        }

        rows.Add(new[]
        {
            InlineKeyboardButton.WithCallbackData("⬅️", $"tq:list:{Math.Max(1, page - 1)}:{categoryIndex}:{subcategoryIndex}"),
            InlineKeyboardButton.WithCallbackData($"{page}/{pages}", $"tq:list:{page}:{categoryIndex}:{subcategoryIndex}"),
            InlineKeyboardButton.WithCallbackData("➡️", $"tq:list:{Math.Min(pages, page + 1)}:{categoryIndex}:{subcategoryIndex}")
        });

        rows.Add(new[]
        {
            InlineKeyboardButton.WithCallbackData("📁 Категории", "tq:cats:1"),
            InlineKeyboardButton.WithCallbackData("📂 Подкатегории", $"tq:subs:{categoryIndex}:1")
        });

        rows.Add(new[]
        {
            InlineKeyboardButton.WithCallbackData("🔄 Сбросить фильтр", "tq:list:1:0:0"),
            InlineKeyboardButton.WithCallbackData("🏠 Меню", "tm:home")
        });

        return new InlineKeyboardMarkup(rows);
    }

    private static async Task SendQuizCategoryPickerAsync(
        ITelegramBotClient bot,
        long chatId,
        int? messageId,
        QuizService quizzes,
        int page,
        CancellationToken ct)
    {
        var categories = await quizzes.GetCategoryStatsAsync(ct);
        var total = categories.Sum(x => x.Count);
        var pages = Math.Max(1, (int)Math.Ceiling(categories.Count / (double)PickerPageSize));
        page = Math.Clamp(page, 1, pages);
        var slice = categories.Skip((page - 1) * PickerPageSize).Take(PickerPageSize).ToList();

        var text = string.Join('\n',
            "<b>📁 Выбор категории</b>",
            $"Всего вопросов: <b>{total}</b>",
            $"Страница <b>{page}/{pages}</b>",
            "",
            "Выбери категорию ниже.");

        var rows = new List<InlineKeyboardButton[]>
        {
            new[] { InlineKeyboardButton.WithCallbackData($"Все категории ({total})", "tq:setcat:0") }
        };

        for (var i = 0; i < slice.Count; i++)
        {
            var globalIndex = (page - 1) * PickerPageSize + i + 1;
            var c = slice[i];
            rows.Add(new[] { InlineKeyboardButton.WithCallbackData($"{c.Name} ({c.Count})", $"tq:setcat:{globalIndex}") });
        }

        rows.Add(new[]
        {
            InlineKeyboardButton.WithCallbackData("⬅️", $"tq:cats:{Math.Max(1, page - 1)}"),
            InlineKeyboardButton.WithCallbackData($"{page}/{pages}", $"tq:cats:{page}"),
            InlineKeyboardButton.WithCallbackData("➡️", $"tq:cats:{Math.Min(pages, page + 1)}")
        });
        rows.Add(new[] { InlineKeyboardButton.WithCallbackData("⬅️ К списку", "tq:list:1:0:0") });

        await SendOrEditTextAsync(bot, chatId, messageId, text, new InlineKeyboardMarkup(rows), ct);
    }

    private static async Task SendQuizSubcategoryPickerAsync(
        ITelegramBotClient bot,
        long chatId,
        int? messageId,
        QuizService quizzes,
        int categoryIndex,
        int page,
        CancellationToken ct)
    {
        var category = await ResolveCategoryAsync(quizzes, categoryIndex, ct);
        var subcategories = await quizzes.GetSubcategoryStatsAsync(category, ct);
        var total = subcategories.Sum(x => x.Count);
        var pages = Math.Max(1, (int)Math.Ceiling(subcategories.Count / (double)PickerPageSize));
        page = Math.Clamp(page, 1, pages);
        var slice = subcategories.Skip((page - 1) * PickerPageSize).Take(PickerPageSize).ToList();

        var text = string.Join('\n',
            "<b>📂 Выбор подкатегории</b>",
            $"Категория: <b>{Html(category ?? "Все категории")}</b>",
            $"Вопросов: <b>{total}</b>. Страница <b>{page}/{pages}</b>",
            "",
            categoryIndex == 0 ? "Сначала можно выбрать категорию, но общий список подкатегорий тоже доступен." : "Выбери подкатегорию ниже.");

        var rows = new List<InlineKeyboardButton[]>
        {
            new[] { InlineKeyboardButton.WithCallbackData($"Все подкатегории ({total})", $"tq:setsub:{categoryIndex}:0") }
        };

        for (var i = 0; i < slice.Count; i++)
        {
            var globalIndex = (page - 1) * PickerPageSize + i + 1;
            var s = slice[i];
            rows.Add(new[] { InlineKeyboardButton.WithCallbackData($"{s.Name} ({s.Count})", $"tq:setsub:{categoryIndex}:{globalIndex}") });
        }

        rows.Add(new[]
        {
            InlineKeyboardButton.WithCallbackData("⬅️", $"tq:subs:{categoryIndex}:{Math.Max(1, page - 1)}"),
            InlineKeyboardButton.WithCallbackData($"{page}/{pages}", $"tq:subs:{categoryIndex}:{page}"),
            InlineKeyboardButton.WithCallbackData("➡️", $"tq:subs:{categoryIndex}:{Math.Min(pages, page + 1)}")
        });
        rows.Add(new[]
        {
            InlineKeyboardButton.WithCallbackData("📁 Категории", "tq:cats:1"),
            InlineKeyboardButton.WithCallbackData("⬅️ К списку", $"tq:list:1:{categoryIndex}:0")
        });

        await SendOrEditTextAsync(bot, chatId, messageId, text, new InlineKeyboardMarkup(rows), ct);
    }

    private static async Task SendQuizCardAsync(
        ITelegramBotClient bot,
        long chatId,
        int? messageId,
        QuizService quizzes,
        long quizId,
        int page,
        int categoryIndex,
        int subcategoryIndex,
        CancellationToken ct)
    {
        var q = await quizzes.GetByIdAsync(quizId, ct);
        if (q == null)
        {
            await SendOrEditTextAsync(bot, chatId, messageId, $"❌ Вопрос {quizId} не найден.", new InlineKeyboardMarkup(new[]
            {
                new[] { InlineKeyboardButton.WithCallbackData("⬅️ К списку", $"tq:list:{page}:{categoryIndex}:{subcategoryIndex}") }
            }), ct);
            return;
        }

        var options = string.IsNullOrWhiteSpace(q.Options)
            ? "-"
            : string.Join("\n", q.Options.Split('|', StringSplitOptions.RemoveEmptyEntries).Select((x, i) => $"{i + 1}. {Html(x)}"));

        var text = string.Join('\n',
            "<b>🧩 Карточка вопроса</b>",
            $"<b>ID:</b> <code>{q.Id}</code>",
            $"<b>Тип:</b> {Html(q.Type)}",
            $"<b>Раздел:</b> {Html(q.Category)} / {Html(q.Subcategory)}",
            "",
            "<b>Вопрос:</b>",
            Html(q.Question),
            "",
            "<b>Ответ:</b>",
            Html(string.IsNullOrWhiteSpace(q.Answer) ? GetPollAnswer(q) : q.Answer),
            "",
            "<b>Варианты:</b>",
            options,
            "",
            "<b>Объяснение:</b>",
            Html(string.IsNullOrWhiteSpace(q.Explanation) ? "-" : q.Explanation),
            string.IsNullOrWhiteSpace(q.Image) ? string.Empty : $"\n<b>Картинка:</b> <code>{Html(q.Image)}</code>");

        var keyboard = new InlineKeyboardMarkup(new[]
        {
            new[] { InlineKeyboardButton.WithCallbackData("⬅️ К списку", $"tq:list:{page}:{categoryIndex}:{subcategoryIndex}") },
            new[] { InlineKeyboardButton.WithCallbackData("🗑️ Удалить", $"tq:del:{q.Id}:{page}:{categoryIndex}:{subcategoryIndex}") }
        });

        await SendOrEditTextAsync(bot, chatId, messageId, text, keyboard, ct);
    }

    private static string GetPollAnswer(TelegramQuizBot.Data.Entities.QuizQuestion q)
    {
        if (q.CorrectOptionId == null || string.IsNullOrWhiteSpace(q.Options)) return "-";
        var options = q.Options.Split('|', StringSplitOptions.None);
        var index = q.CorrectOptionId.Value;
        return index >= 0 && index < options.Length ? options[index] : "-";
    }

    private static async Task<string?> ResolveCategoryAsync(QuizService quizzes, int categoryIndex, CancellationToken ct)
    {
        if (categoryIndex <= 0) return null;
        var categories = await quizzes.GetCategoryStatsAsync(ct);
        return categoryIndex <= categories.Count ? categories[categoryIndex - 1].Name : null;
    }

    private static async Task<string?> ResolveSubcategoryAsync(QuizService quizzes, string? category, int subcategoryIndex, CancellationToken ct)
    {
        if (subcategoryIndex <= 0) return null;
        var subcategories = await quizzes.GetSubcategoryStatsAsync(category, ct);
        return subcategoryIndex <= subcategories.Count ? subcategories[subcategoryIndex - 1].Name : null;
    }

    private static async Task SendOrEditTextAsync(
        ITelegramBotClient bot,
        long chatId,
        int? messageId,
        string text,
        InlineKeyboardMarkup? keyboard,
        CancellationToken ct)
    {
        if (messageId is { } id)
        {
            try
            {
                await bot.EditMessageTextAsync(chatId, id, text, parseMode: ParseMode.Html, replyMarkup: keyboard, cancellationToken: ct);
                return;
            }
            catch
            {
                // If Telegram refuses editing old/unchanged message, fall back to a new one.
            }
        }

        await bot.SendTextMessageAsync(chatId, text, parseMode: ParseMode.Html, replyMarkup: keyboard, cancellationToken: ct);
    }

    private static int ReadInt(string[] parts, int index, int fallback)
    {
        return parts.Length > index && int.TryParse(parts[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;
    }

    private static long ReadLong(string[] parts, int index, long fallback)
    {
        return parts.Length > index && long.TryParse(parts[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;
    }

    private static async Task SendStudentListAsync(ITelegramBotClient bot, long chatId, StudentDirectoryService directory, string? query, CancellationToken ct)
    {
        var page = await directory.SearchAsync(query, 12, includeHidden: false, ct);
        if (page.Items.Count == 0)
        {
            await bot.SendTextMessageAsync(chatId, string.IsNullOrWhiteSpace(query)
                ? "👥 Пока student-боту никто не писал."
                : $"🔎 По запросу '{query}' ничего не найдено.", cancellationToken: ct);
            return;
        }

        var text = BuildStudentTable(page);
        var keyboard = page.Items
            .Select(item => new[]
            {
                InlineKeyboardButton.WithCallbackData($"👤 {Trim(DisplayName(item.Contact), 28)}", $"sq:card:{item.Contact.UserId}")
            })
            .ToArray();

        await bot.SendTextMessageAsync(
            chatId,
            text,
            parseMode: ParseMode.Html,
            replyMarkup: new InlineKeyboardMarkup(keyboard),
            cancellationToken: ct);
    }

    private static async Task SendStudentCardAsync(ITelegramBotClient bot, long chatId, StudentDirectoryService directory, long studentId, CancellationToken ct)
    {
        var card = await directory.GetCardAsync(studentId, ct);
        if (card == null)
        {
            await bot.SendTextMessageAsync(chatId, $"❌ Контакт {studentId} не найден. Если надо выдать доступ вручную: /grant_user {studentId}", cancellationToken: ct);
            return;
        }

        var c = card.Contact;
        var p = card.Progress;
        var text = string.Join('\n',
            "<b>👤 Карточка ученика</b>",
            $"<b>ID:</b> <code>{c.UserId}</code>",
            $"<b>Имя:</b> {Html(DisplayName(c))}",
            $"<b>Username:</b> {Html(c.Username is null ? "-" : "@" + c.Username)}",
            $"<b>Доступ:</b> {AccessText(card.AccessState)}",
            $"<b>Сообщений:</b> {c.MessageCount}",
            $"<b>Первый раз:</b> {FormatDate(c.FirstSeenAt)}",
            $"<b>Последний раз:</b> {FormatDate(c.LastSeenAt)}",
            $"<b>Последнее сообщение:</b> {Html(Trim(c.LastMessageText ?? "-", 160))}",
            p == null ? "<b>Прогресс:</b> -" : $"<b>Прогресс:</b> всего {p.Total}, ✅ {p.Correct}, ❌ {p.Incorrect}, ⭐ {p.Experience}");

        var keyboard = new InlineKeyboardMarkup(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("✅ 24 часа", $"sq:grant24:{c.UserId}"),
                InlineKeyboardButton.WithCallbackData("✅ 7 дней", $"sq:grant7:{c.UserId}")
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData("✅ Навсегда", $"sq:grantForever:{c.UserId}"),
                InlineKeyboardButton.WithCallbackData("⛔ Забрать", $"sq:revoke:{c.UserId}")
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData("🙈 Скрыть", $"sq:hide:{c.UserId}"),
                InlineKeyboardButton.WithCallbackData("🗑️ Удалить контакт", $"sq:delete:{c.UserId}")
            }
        });

        await bot.SendTextMessageAsync(chatId, text, parseMode: ParseMode.Html, replyMarkup: keyboard, cancellationToken: ct);
    }

    private static async Task HandleStudentCleanAsync(ITelegramBotClient bot, Message message, string text, StudentDirectoryService directory, CancellationToken ct)
    {
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            await bot.SendTextMessageAsync(message.Chat.Id, TelegramText.StudentCleanHelp, cancellationToken: ct);
            return;
        }

        var mode = parts[1].ToLowerInvariant();
        switch (mode)
        {
            case "logs":
            {
                var days = ParseDays(parts, 2, 90);
                var count = await directory.DeleteStartLogsOlderThanAsync(days, ct);
                await bot.SendTextMessageAsync(message.Chat.Id, $"✅ Очищено записей start_log старше {days} дней: {count}.", cancellationToken: ct);
                break;
            }
            case "denied":
            case "unapproved":
            {
                var days = ParseDays(parts, 2, 30);
                var count = await directory.DeleteContactsWithoutActiveAccessAsync(days, includeHidden: true, ct);
                await bot.SendTextMessageAsync(message.Chat.Id, $"✅ Удалено контактов без активного доступа старше {days} дней: {count}.", cancellationToken: ct);
                break;
            }
            case "hidden":
            {
                var days = ParseDays(parts, 2, 30);
                var count = await directory.DeleteHiddenContactsAsync(days, ct);
                await bot.SendTextMessageAsync(message.Chat.Id, $"✅ Удалено скрытых контактов старше {days} дней: {count}.", cancellationToken: ct);
                break;
            }
            case "contact":
            {
                if (parts.Length < 3 || !long.TryParse(parts[2], out var studentId))
                {
                    await bot.SendTextMessageAsync(message.Chat.Id, "❌ Формат: /student_clean contact user_id", cancellationToken: ct);
                    return;
                }

                var deleteAccess = parts.Contains("access", StringComparer.OrdinalIgnoreCase);
                var deleteProgress = parts.Contains("progress", StringComparer.OrdinalIgnoreCase);
                var deleteLogs = parts.Contains("logs", StringComparer.OrdinalIgnoreCase);
                var count = await directory.DeleteContactAsync(studentId, deleteAccess, deleteProgress, deleteLogs, ct);
                await bot.SendTextMessageAsync(message.Chat.Id, $"✅ Удаление по ученику {studentId}: затронуто записей {count}. Access={deleteAccess}, Progress={deleteProgress}, Logs={deleteLogs}.", cancellationToken: ct);
                break;
            }
            case "rebuild":
            {
                var count = await directory.RebuildContactsFromExistingDataAsync(ct);
                await bot.SendTextMessageAsync(message.Chat.Id, $"✅ Справочник student_contacts перестроен/дополнен. Затронуто контактов: {count}.", cancellationToken: ct);
                break;
            }
            default:
                await bot.SendTextMessageAsync(message.Chat.Id, TelegramText.StudentCleanHelp, cancellationToken: ct);
                break;
        }
    }

    private static string BuildStudentTable(StudentDirectoryPage page)
    {
        var header = page.IsSearch
            ? $"<b>🔎 Найдены ученики: {Html(page.Query ?? string.Empty)}</b>"
            : "<b>👥 Последние ученики, писавшие student-боту</b>";

        var rows = new List<string>
        {
            header,
            "<pre>ID           Доступ     Сообщ  Последний визит  Имя"
        };

        foreach (var item in page.Items)
        {
            var c = item.Contact;
            rows.Add(string.Format(CultureInfo.InvariantCulture,
                "{0,-12} {1,-9} {2,5}  {3,-15} {4}",
                Trim(c.UserId.ToString(CultureInfo.InvariantCulture), 12),
                AccessShort(item.AccessState),
                c.MessageCount,
                FormatDate(c.LastSeenAt),
                Html(Trim(DisplayName(c), 24))));
        }

        rows.Add("</pre>");
        rows.Add("Нажми кнопку ученика ниже, чтобы открыть карточку и выдать доступ.");
        return string.Join('\n', rows);
    }

    private static async Task HandleTechnicalBreakAsync(ITelegramBotClient bot, Message message, string text, TechnicalBreakService service, CancellationToken ct)
    {
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            await bot.SendTextMessageAsync(message.Chat.Id, "❌ Формат: /technical_break [on|off] [ЧЧ:ММ]", cancellationToken: ct);
            return;
        }

        if (parts[1].Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            await service.DisableAsync(ct);
            await bot.SendTextMessageAsync(message.Chat.Id, "✅ Технический перерыв отменён.", cancellationToken: ct);
            return;
        }

        if (!parts[1].Equals("on", StringComparison.OrdinalIgnoreCase) || parts.Length < 3 || !TimeOnly.TryParse(parts[2], out var time))
        {
            await bot.SendTextMessageAsync(message.Chat.Id, "❌ Формат: /technical_break on ЧЧ:ММ", cancellationToken: ct);
            return;
        }

        var now = DateTimeOffset.Now;
        var localEnd = new DateTimeOffset(now.Year, now.Month, now.Day, time.Hour, time.Minute, 0, now.Offset);
        if (localEnd <= now) localEnd = localEnd.AddDays(1);
        await service.SetAsync(localEnd.ToUniversalTime(), ct);
        await bot.SendTextMessageAsync(message.Chat.Id, $"✅ Технический перерыв установлен до {localEnd:HH:mm}.", cancellationToken: ct);
    }

    private Task HandleErrorAsync(ITelegramBotClient bot, Exception exception, CancellationToken ct)
    {
        if (TelegramPollingErrorClassifier.IsExpectedLongPollingTimeout(exception))
        {
            TelegramDebugTrace.Exception("teacher.error", "long-polling-timeout", exception);
            _logger.LogDebug("Teacher bot long polling timeout");
            return Task.CompletedTask;
        }

        if (TelegramPollingErrorClassifier.IsTransientTelegramApiError(exception))
        {
            TelegramDebugTrace.Exception("teacher.error", "transient", exception);
            _logger.LogWarning("Teacher bot transient Telegram polling error: {Message}", exception.Message);
            return Task.Delay(TimeSpan.FromSeconds(5), ct);
        }

        TelegramDebugTrace.Exception("teacher.error", "fatal", exception);
        _logger.LogError(exception, "Teacher bot polling error");
        return Task.CompletedTask;
    }

    private static bool TryReadLongArgument(string text, out long value)
    {
        value = 0;

        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2
               && long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static string StripCommand(string text)
    {
        var index = text.IndexOf(' ');
        return index < 0 ? string.Empty : text[(index + 1)..].Trim();
    }

    private static int ParseDays(string[] parts, int index, int defaultValue)
    {
        return parts.Length > index && int.TryParse(parts[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out var days)
            ? Math.Clamp(days, 0, 3650)
            : defaultValue;
    }

    private static string DisplayName(TelegramQuizBot.Data.Entities.StudentContact contact)
    {
        if (!string.IsNullOrWhiteSpace(contact.FullName)) return contact.FullName!;
        if (!string.IsNullOrWhiteSpace(contact.Username)) return "@" + contact.Username;
        return $"ID {contact.UserId}";
    }

    private static string AccessText(StudentAccessState state) => state switch
    {
        StudentAccessState.Permanent => "✅ постоянный",
        StudentAccessState.Temporary => "✅ временный",
        StudentAccessState.Expired => "⌛ истёк",
        _ => "❌ нет"
    };

    private static string AccessShort(StudentAccessState state) => state switch
    {
        StudentAccessState.Permanent => "forever",
        StudentAccessState.Temporary => "active",
        StudentAccessState.Expired => "expired",
        _ => "none"
    };

    private static string FormatDate(DateTimeOffset value) => value.ToLocalTime().ToString("dd.MM HH:mm", CultureInfo.InvariantCulture);

    private static string Html(string value) => WebUtility.HtmlEncode(value);

    private static string Trim(string? value, int max)
    {
        value ??= string.Empty;
        return value.Length <= max ? value : value[..max] + "…";
    }
}
