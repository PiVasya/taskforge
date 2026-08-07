using TaskForge.Browser.Api.Configuration;
using TaskForge.Browser.Api.Security;

namespace TaskForge.Browser.Api.Services;

public sealed class BrowserCapacityGate : IDisposable
{
    private readonly SemaphoreSlim _semaphore;
    private readonly TimeSpan _waitTimeout;

    public BrowserCapacityGate(BrowserOptions options)
    {
        var capacity = System.Math.Clamp(options.MaxConcurrentOperations, 1, 64);
        _semaphore = new SemaphoreSlim(capacity, capacity);
        _waitTimeout = TimeSpan.FromSeconds(System.Math.Clamp(options.ActionTimeoutSeconds, 1, 60));
    }

    public async Task<IAsyncDisposable> EnterAsync(CancellationToken cancellationToken)
    {
        if (!await _semaphore.WaitAsync(_waitTimeout, cancellationToken))
        {
            throw new BrowserApiException(StatusCodes.Status503ServiceUnavailable, "BROWSER_CAPACITY_EXHAUSTED", "Все браузерные слоты сейчас заняты. Повторите запрос немного позже.", 2);
        }

        return new Lease(_semaphore);
    }

    public void Dispose() => _semaphore.Dispose();

    private sealed class Lease(SemaphoreSlim semaphore) : IAsyncDisposable
    {
        private SemaphoreSlim? _semaphore = semaphore;

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _semaphore, null)?.Release();
            return ValueTask.CompletedTask;
        }
    }
}
