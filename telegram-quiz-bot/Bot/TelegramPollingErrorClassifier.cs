using System.Net.Http;
using System.Net.Sockets;
using Telegram.Bot.Exceptions;

namespace TelegramQuizBot.Bot;

public static class TelegramPollingErrorClassifier
{
    public static bool IsExpectedLongPollingTimeout(Exception exception)
    {
        return exception is RequestException && HasInner<TaskCanceledException>(exception);
    }

    public static bool IsTransientTelegramApiError(Exception exception)
    {
        if (exception is ApiRequestException api)
        {
            return api.ErrorCode is 429 or 500 or 502 or 503 or 504
                   || api.Message.Contains("Bad Gateway", StringComparison.OrdinalIgnoreCase)
                   || api.Message.Contains("Too Many Requests", StringComparison.OrdinalIgnoreCase);
        }

        return HasInner<HttpRequestException>(exception)
               || HasInner<IOException>(exception)
               || HasInner<SocketException>(exception);
    }

    private static bool HasInner<T>(Exception exception) where T : Exception
    {
        for (var current = exception; current != null; current = current.InnerException!)
        {
            if (current is T) return true;
        }

        return false;
    }
}
