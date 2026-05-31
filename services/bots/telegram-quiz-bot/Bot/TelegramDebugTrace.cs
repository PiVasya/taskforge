namespace TelegramQuizBot.Bot;

public static class TelegramDebugTrace
{
    private static readonly object Sync = new();

    public static bool Enabled => !string.Equals(
        Environment.GetEnvironmentVariable("TELEGRAM_QUIZ_VERBOSE_CONSOLE_LOGS"),
        "false",
        StringComparison.OrdinalIgnoreCase);

    public static void Write(string area, string action, params (string Name, object? Value)[] values)
    {
        if (!Enabled) return;

        try
        {
            var parts = values
                .Select(x => $"{x.Name}={Format(x.Value)}");

            lock (Sync)
            {
                Console.WriteLine($"[TG-QUIZ-TRACE] {DateTimeOffset.UtcNow:O} area={area} action={action} {string.Join(' ', parts)}");
            }
        }
        catch
        {
            // Debug logging must never break bot processing.
        }
    }

    public static void Exception(string area, string action, Exception exception, params (string Name, object? Value)[] values)
    {
        if (!Enabled) return;

        try
        {
            var baseValues = values
                .Concat(new[]
                {
                    ("exception", (object?)exception.GetType().Name),
                    ("message", (object?)exception.Message)
                });

            Write(area, action, baseValues.ToArray());
        }
        catch
        {
            // Debug logging must never break bot processing.
        }
    }

    private static string Format(object? value)
    {
        if (value is null) return "null";
        var text = value.ToString() ?? string.Empty;
        text = text.Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal);

        if (text.Length > 700)
            text = text[..700] + "...";

        return '"' + text.Replace("\"", "'", StringComparison.Ordinal) + '"';
    }
}
