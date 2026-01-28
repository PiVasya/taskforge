using System.Globalization;

namespace taskforge.Services.ImageTests;

/// <summary>
/// Простой контекст для связки логов "контроллер -> сервис".
///
/// Контроллер задаёт TraceId (и любую полезную мета-инфу),
/// а сервисы пишут его в Console.WriteLine, чтобы по логам было видно,
/// какая конкретно проверка/запрос выполнялся.
/// </summary>
public static class ImageTestTraceContext
{
    private static readonly AsyncLocal<string?> _trace = new();

    public static string? Current => _trace.Value;

    public static IDisposable Begin(string trace)
    {
        var prev = _trace.Value;
        _trace.Value = trace;
        return new Scope(() => _trace.Value = prev);
    }

    public static void ConsoleLog(bool enabled, string message)
    {
        if (!enabled) return;

        var prefix = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
        var trace = Current;
        if (!string.IsNullOrWhiteSpace(trace))
            Console.WriteLine($"[{prefix}] [image-test] [{trace}] {message}");
        else
            Console.WriteLine($"[{prefix}] [image-test] {message}");
    }

    private sealed class Scope : IDisposable
    {
        private readonly Action _onDispose;
        private int _disposed;

        public Scope(Action onDispose) => _onDispose = onDispose;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            _onDispose();
        }
    }
}
