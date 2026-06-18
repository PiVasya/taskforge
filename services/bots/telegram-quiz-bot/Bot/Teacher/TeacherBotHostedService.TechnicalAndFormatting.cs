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
            TelegramDebugTrace.Exception("teacher.error", "long-polling-timeout", exception);
            _logger.LogDebug("Teacher bot long polling timeout");
            return Task.CompletedTask;
        }

        if (TelegramPollingErrorClassifier.IsTransientTelegramApiError(exception))
        {
            TelegramDebugTrace.Exception("teacher.error", "transient", exception);
            _logger.LogWarning("Teacher bot transient Telegram polling error: {Message}", exception.Message);
            return Task.Delay(TimeSpan.FromSeconds(5), ct);
        }

        TelegramDebugTrace.Exception("teacher.error", "fatal", exception);
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
            ? System.Math.Clamp(days, 0, 3650)
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
