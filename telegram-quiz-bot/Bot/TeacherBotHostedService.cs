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
        if (string.IsNullOrWhiteSpace(_options.TeacherBotToken))
        {
            _logger.LogWarning("Teacher bot token is empty. Teacher bot is disabled.");
            return;
        }

        _bot = _clientFactory.Create(_options.TeacherBotToken);
        await _bot.DeleteWebhookAsync(cancellationToken: stoppingToken);
        _bot.StartReceiving(HandleUpdateAsync, HandleErrorAsync, new ReceiverOptions { AllowedUpdates = Array.Empty<UpdateType>() }, stoppingToken);
        _logger.LogInformation("Teacher Telegram bot started");

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
            await HandleCallbackAsync(bot, callback, ct);
        }
    }

    private async Task HandleMessageAsync(ITelegramBotClient bot, Message message, CancellationToken ct)
    {
        var teacherId = message.From?.Id ?? 0;
        if (teacherId == 0) return;

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
            if (await teachers.IsTeacherAsync(teacherId, ct))
                await bot.SendTextMessageAsync(message.Chat.Id, "✅ Вы уже авторизованы как учитель. Используйте /help.", cancellationToken: ct);
            else
                await bot.SendTextMessageAsync(message.Chat.Id, "🔐 Для доступа введите пароль учителя:", cancellationToken: ct);
            return;
        }

        if (!string.IsNullOrWhiteSpace(_options.TeacherPassword) && text == _options.TeacherPassword)
        {
            await teachers.AuthorizeAsync(teacherId, ct);
            await bot.SendTextMessageAsync(message.Chat.Id, "🔓 Авторизация успешна. Теперь доступны команды учителя. Помощников может быть сколько угодно — каждый входит по паролю.", cancellationToken: ct);
            return;
        }

        if (!await teachers.IsTeacherAsync(teacherId, ct))
        {
            await bot.SendTextMessageAsync(message.Chat.Id, "⛔ Доступ запрещён. Введите пароль учителя.", cancellationToken: ct);
            return;
        }

        if (message.Photo is { Length: > 0 } && _state.Drafts.TryGetValue(teacherId, out var photoDraft) && photoDraft.Step == "image")
        {
            var fileId = message.Photo.OrderByDescending(x => x.FileSize ?? 0).First().FileId;
            var file = await bot.GetFileAsync(fileId, ct);
            await using var stream = new MemoryStream();
            await bot.DownloadFileAsync(file.FilePath!, stream, ct);
            stream.Position = 0;
            photoDraft.ImageKey = await imageStorage.SaveImageAsync(stream, "image/jpeg", ".jpg", ct);
            photoDraft.Step = "question";
            await bot.SendTextMessageAsync(message.Chat.Id, "✅ Изображение сохранено в MinIO. Теперь введите текст вопроса:", cancellationToken: ct);
            return;
        }

        if (message.Poll is { } poll)
        {
            await SavePollQuizAsync(bot, message, poll, quizzes, ct);
            return;
        }

        if (string.IsNullOrWhiteSpace(text)) return;

        if (_state.Drafts.TryGetValue(teacherId, out var draft))
        {
            await ContinueDraftAsync(bot, message, quizzes, draft, ct);
            return;
        }

        if (text.StartsWith("/help"))
        {
            await bot.SendTextMessageAsync(message.Chat.Id, TelegramText.TeacherHelp, cancellationToken: ct);
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
            var list = await quizzes.ListAsync(null, 0, 20, ct);
            var lines = list.Select(x => $"{x.Id}. [{x.Type}] {x.Category}/{x.Subcategory}: {Trim(x.Question, 80)}");
            await bot.SendTextMessageAsync(message.Chat.Id, list.Count == 0 ? "Вопросов пока нет." : string.Join('\n', lines), cancellationToken: ct);
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
            await bot.SendTextMessageAsync(message.Chat.Id, "Неизвестная команда. Используйте /help.", cancellationToken: ct);
        }
    }

    private async Task HandleCallbackAsync(ITelegramBotClient bot, CallbackQuery callback, CancellationToken ct)
    {
        var teacherId = callback.From.Id;
        var chatId = callback.Message?.Chat.Id ?? teacherId;
        var data = callback.Data ?? string.Empty;

        using var scope = _provider.CreateScope();
        var teachers = scope.ServiceProvider.GetRequiredService<TeacherAccessService>();
        if (!await teachers.IsTeacherAsync(teacherId, ct))
        {
            await bot.AnswerCallbackQueryAsync(callback.Id, "Доступ запрещён", showAlert: true, cancellationToken: ct);
            return;
        }

        var students = scope.ServiceProvider.GetRequiredService<StudentAccessService>();
        var directory = scope.ServiceProvider.GetRequiredService<StudentDirectoryService>();

        try
        {
            var parts = data.Split(':', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3 || parts[0] != "sq")
            {
                await bot.AnswerCallbackQueryAsync(callback.Id, cancellationToken: ct);
                return;
            }

            var action = parts[1];
            if (!long.TryParse(parts[2], out var studentId))
            {
                await bot.AnswerCallbackQueryAsync(callback.Id, "Некорректный ID", showAlert: true, cancellationToken: ct);
                return;
            }

            switch (action)
            {
                case "card":
                    await SendStudentCardAsync(bot, chatId, directory, studentId, ct);
                    break;
                case "grant24":
                    await students.AddAsync(studentId, 24, ct);
                    await bot.AnswerCallbackQueryAsync(callback.Id, "Доступ на 24 часа выдан", cancellationToken: ct);
                    await SendStudentCardAsync(bot, chatId, directory, studentId, ct);
                    break;
                case "grant7":
                    await students.AddAsync(studentId, 24 * 7, ct);
                    await bot.AnswerCallbackQueryAsync(callback.Id, "Доступ на 7 дней выдан", cancellationToken: ct);
                    await SendStudentCardAsync(bot, chatId, directory, studentId, ct);
                    break;
                case "grantForever":
                    await students.AddAsync(studentId, null, ct);
                    await bot.AnswerCallbackQueryAsync(callback.Id, "Постоянный доступ выдан", cancellationToken: ct);
                    await SendStudentCardAsync(bot, chatId, directory, studentId, ct);
                    break;
                case "revoke":
                    await students.RemoveAsync(studentId, ct);
                    await bot.AnswerCallbackQueryAsync(callback.Id, "Доступ отозван", cancellationToken: ct);
                    await SendStudentCardAsync(bot, chatId, directory, studentId, ct);
                    break;
                case "hide":
                    await directory.HideContactAsync(studentId, teacherId, ct);
                    await bot.AnswerCallbackQueryAsync(callback.Id, "Контакт скрыт из общего списка", cancellationToken: ct);
                    break;
                case "delete":
                    await directory.DeleteContactAsync(studentId, deleteAccess: false, deleteProgress: false, deleteLogs: false, ct);
                    await bot.AnswerCallbackQueryAsync(callback.Id, "Контакт удалён из справочника", cancellationToken: ct);
                    break;
                default:
                    await bot.AnswerCallbackQueryAsync(callback.Id, cancellationToken: ct);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Teacher callback handling failed");
            await bot.AnswerCallbackQueryAsync(callback.Id, "Ошибка обработки кнопки", showAlert: true, cancellationToken: ct);
        }
    }

    private static async Task SavePollQuizAsync(ITelegramBotClient bot, Message message, Poll poll, QuizService quizzes, CancellationToken ct)
    {
        if (!string.Equals(poll.Type, "quiz", StringComparison.OrdinalIgnoreCase) || poll.CorrectOptionId is null)
        {
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
            _logger.LogDebug("Teacher bot long polling timeout");
            return Task.CompletedTask;
        }

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
