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

}
