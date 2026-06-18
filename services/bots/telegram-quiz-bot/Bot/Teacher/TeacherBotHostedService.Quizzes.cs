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
}
