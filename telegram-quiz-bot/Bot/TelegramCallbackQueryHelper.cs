using Telegram.Bot;
using Telegram.Bot.Exceptions;

namespace TelegramQuizBot.Bot;

public static class TelegramCallbackQueryHelper
{
    public static async Task SafeAnswerCallbackQueryAsync(
        this ITelegramBotClient bot,
        string callbackQueryId,
        ILogger logger,
        string? text = null,
        bool? showAlert = null,
        string? url = null,
        int? cacheTime = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await bot.AnswerCallbackQueryAsync(
                callbackQueryId,
                text,
                showAlert,
                url,
                cacheTime,
                cancellationToken);
        }
        catch (ApiRequestException ex) when (IsOldOrInvalidCallbackQuery(ex))
        {
            logger.LogDebug("Ignored expired Telegram callback query: {Message}", ex.Message);
        }
    }

    private static bool IsOldOrInvalidCallbackQuery(ApiRequestException ex)
    {
        return ex.Message.Contains("query is too old", StringComparison.OrdinalIgnoreCase)
               || ex.Message.Contains("response timeout expired", StringComparison.OrdinalIgnoreCase)
               || ex.Message.Contains("query ID is invalid", StringComparison.OrdinalIgnoreCase);
    }
}
