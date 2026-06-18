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

public sealed partial class StudentBotHostedService
{
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

}
