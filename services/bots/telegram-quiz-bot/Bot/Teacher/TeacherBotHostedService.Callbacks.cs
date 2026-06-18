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

}
