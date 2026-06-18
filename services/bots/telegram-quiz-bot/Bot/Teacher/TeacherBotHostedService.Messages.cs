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

public sealed partial class TeacherBotHostedService
{
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

}
