using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
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
    private readonly TelegramBotClientFactory _botClientFactory;
    private TelegramBotClient? _bot;

    public TeacherBotHostedService(
        IServiceProvider provider,
        ILogger<TeacherBotHostedService> logger,
        IOptions<TelegramQuizOptions> options,
        TeacherBotStateStore state,
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
        if (string.IsNullOrWhiteSpace(_options.TeacherBotToken))
        {
            _logger.LogWarning("Teacher bot token is empty. Teacher bot is disabled.");
            return;
        }

        _bot = _botClientFactory.Create(_options.TeacherBotToken);
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
    }

    private async Task HandleMessageAsync(ITelegramBotClient bot, Message message, CancellationToken ct)
    {
        var userId = message.From?.Id ?? 0;
        if (userId == 0) return;

        using var scope = _provider.CreateScope();
        var teachers = scope.ServiceProvider.GetRequiredService<TeacherAccessService>();
        var students = scope.ServiceProvider.GetRequiredService<StudentAccessService>();
        var categories = scope.ServiceProvider.GetRequiredService<CategoryService>();
        var quizzes = scope.ServiceProvider.GetRequiredService<QuizService>();
        var breaks = scope.ServiceProvider.GetRequiredService<TechnicalBreakService>();
        var stats = scope.ServiceProvider.GetRequiredService<StatisticsService>();
        var imageStorage = scope.ServiceProvider.GetRequiredService<IS3ImageStorage>();

        var text = message.Text?.Trim();

        if (text == "/start")
        {
            if (await teachers.IsTeacherAsync(userId, ct))
                await bot.SendTextMessageAsync(message.Chat.Id, "✅ Вы уже авторизованы как учитель. Используйте /help.", cancellationToken: ct);
            else
                await bot.SendTextMessageAsync(message.Chat.Id, "🔐 Для доступа введите пароль:", cancellationToken: ct);
            return;
        }

        if (!string.IsNullOrWhiteSpace(_options.TeacherPassword) && text == _options.TeacherPassword)
        {
            await teachers.AuthorizeAsync(userId, ct);
            await bot.SendTextMessageAsync(message.Chat.Id, "🔓 Авторизация успешна. Теперь доступны команды учителя.", cancellationToken: ct);
            return;
        }

        if (!await teachers.IsTeacherAsync(userId, ct))
        {
            await bot.SendTextMessageAsync(message.Chat.Id, "⛔ Доступ запрещён.", cancellationToken: ct);
            return;
        }

        if (message.Photo is { Length: > 0 } && _state.Drafts.TryGetValue(userId, out var photoDraft) && photoDraft.Step == "image")
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
            return;
        }

        if (string.IsNullOrWhiteSpace(text)) return;

        if (_state.Drafts.TryGetValue(userId, out var draft))
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
            await teachers.LogoutAsync(userId, ct);
            await bot.SendTextMessageAsync(message.Chat.Id, "✅ Вы вышли из аккаунта учителя.", cancellationToken: ct);
        }
        else if (text.StartsWith("/add_user"))
        {
            var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !long.TryParse(parts[1], out var studentId))
            {
                await bot.SendTextMessageAsync(message.Chat.Id, "❌ Формат: /add_user user_id [hours]", cancellationToken: ct);
                return;
            }
            int? hours = null;
            if (parts.Length > 2 && int.TryParse(parts[2], out var parsedHours)) hours = parsedHours;
            await students.AddAsync(studentId, hours, ct);
            await bot.SendTextMessageAsync(message.Chat.Id, hours is > 0 ? $"✅ Ученик {studentId} добавлен на {hours} часов." : $"✅ Ученик {studentId} добавлен навсегда.", cancellationToken: ct);
        }
        else if (text.StartsWith("/remove_user"))
        {
            var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !long.TryParse(parts[1], out var studentId))
            {
                await bot.SendTextMessageAsync(message.Chat.Id, "❌ Формат: /remove_user user_id", cancellationToken: ct);
                return;
            }
            var removed = await students.RemoveAsync(studentId, ct);
            await bot.SendTextMessageAsync(message.Chat.Id, removed ? $"✅ Ученик {studentId} удалён." : $"❌ Ученик {studentId} не найден.", cancellationToken: ct);
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
            await bot.SendTextMessageAsync(message.Chat.Id, "📝 Отправьте Telegram-опрос типа quiz. В этом первом C#-переносе он сохранится в категорию 'Остальное'.", cancellationToken: ct);
        }
        else if (text.StartsWith("/add_text_question"))
        {
            _state.Drafts[userId] = new TeacherDraftQuestion { Type = "text", Step = "image" };
            await bot.SendTextMessageAsync(message.Chat.Id, "📸 Отправьте изображение для вопроса или напишите /skip.", cancellationToken: ct);
        }
        else if (text.StartsWith("/skip"))
        {
            _state.Drafts[userId] = new TeacherDraftQuestion { Type = "text", Step = "question" };
            await bot.SendTextMessageAsync(message.Chat.Id, "📝 Введите текст вопроса:", cancellationToken: ct);
        }
        else if (text.StartsWith("/remove_quiz"))
        {
            var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !long.TryParse(parts[1], out var id))
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

    private async Task ContinueDraftAsync(ITelegramBotClient bot, Message message, QuizService quizzes, TeacherDraftQuestion draft, CancellationToken ct)
    {
        var userId = message.From!.Id;
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
                _state.Drafts.TryRemove(userId, out _);
                await bot.SendTextMessageAsync(message.Chat.Id, $"✅ Текстовый вопрос сохранён. ID: {saved.Id}", cancellationToken: ct);
                break;
        }
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
        if (TelegramPollingErrorClassifier.IsExpectedShutdownOrLongPollingTimeout(exception, ct))
        {
            _logger.LogDebug("Teacher bot long polling timeout or shutdown signal");
            return Task.CompletedTask;
        }

        _logger.LogError(exception, "Teacher bot polling error");
        return Task.CompletedTask;
    }

    private static string Trim(string value, int max) => value.Length <= max ? value : value[..max] + "…";
}
