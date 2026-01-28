using System;

namespace taskforge.Helpers;

/// <summary>
/// Super-simple console logger for debugging in Docker/GitHub Actions.
/// The project also uses ILogger, but Console.WriteLine is sometimes the most reliable
/// way to ensure logs show up in runner output.
/// </summary>
public static class DebugConsole
{
    public static void Log(string tag, string message)
    {
        // ISO-8601 UTC timestamp so logs are easy to correlate across services.
        Console.WriteLine($"[{DateTime.UtcNow:O}] [{tag}] {message}");
    }

    public static void Error(string tag, Exception ex, string? message = null)
    {
        if (!string.IsNullOrWhiteSpace(message))
            Log(tag, message);

        Console.WriteLine($"[{DateTime.UtcNow:O}] [{tag}] EXCEPTION: {ex.GetType().FullName}: {ex.Message}\n{ex.StackTrace}");
    }
}
