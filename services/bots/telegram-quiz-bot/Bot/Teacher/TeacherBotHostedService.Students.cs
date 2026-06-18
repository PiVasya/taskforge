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

}
