using Telegram.Bot.Exceptions;

namespace TelegramQuizBot.Bot;

public static class TelegramPollingErrorClassifier
{
    public static bool IsExpectedLongPollingTimeout(Exception exception)
    {
        return exception is RequestException && HasInner<TaskCanceledException>(exception);
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
