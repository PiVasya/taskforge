using Telegram.Bot.Exceptions;

namespace TelegramQuizBot.Bot;

public static class TelegramPollingErrorClassifier
{
    public static bool IsExpectedShutdownOrLongPollingTimeout(Exception exception, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return true;

        if (exception is OperationCanceledException or TaskCanceledException or TimeoutException)
            return true;

        if (exception is RequestException requestException && ContainsTimeout(requestException))
            return true;

        return ContainsTimeout(exception);
    }

    private static bool ContainsTimeout(Exception exception)
    {
        for (var current = exception; current != null; current = current.InnerException!)
        {
            if (current is TaskCanceledException or TimeoutException)
                return true;

            var message = current.Message;
            if (message.Contains("timed out", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("operation was canceled", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
