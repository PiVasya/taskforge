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
