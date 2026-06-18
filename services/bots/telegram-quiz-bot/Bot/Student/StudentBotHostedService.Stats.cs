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

}
