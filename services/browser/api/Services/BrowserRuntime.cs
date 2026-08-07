using Microsoft.Playwright;
using TaskForge.Browser.Api.Configuration;

namespace TaskForge.Browser.Api.Services;

public sealed class BrowserRuntime(BrowserOptions options, ILogger<BrowserRuntime> logger) : IHostedService, IAsyncDisposable
{
    private readonly BrowserOptions _options = options;
    private readonly ILogger<BrowserRuntime> _logger = logger;
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private int _disposed;

    public bool IsReady => _browser?.IsConnected == true;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await EnsureStartedAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => DisposeAsync().AsTask();

    public async Task<IBrowserContext> NewContextAsync(int width, int height, CancellationToken cancellationToken)
    {
        await EnsureStartedAsync(cancellationToken);
        return await _browser!.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = width, Height = height },
            ScreenSize = new ScreenSize { Width = width, Height = height },
            AcceptDownloads = false,
            UserAgent = _options.UserAgent,
            IgnoreHTTPSErrors = _options.IgnoreHttpsErrors,
            Locale = "ru-RU",
            TimezoneId = "Europe/Minsk",
            ReducedMotion = _options.ReduceMotion ? ReducedMotion.Reduce : ReducedMotion.NoPreference,
            ServiceWorkers = ServiceWorkerPolicy.Block
        });
    }

    private async Task EnsureStartedAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (IsReady) return;
        await _startLock.WaitAsync(cancellationToken);
        try
        {
            if (IsReady) return;
            _playwright ??= await Playwright.CreateAsync();
            var args = new List<string>
            {
                "--disable-dev-shm-usage",
                "--disable-background-networking",
                "--disable-default-apps",
                "--disable-extensions",
                "--disable-sync",
                "--metrics-recording-only",
                "--no-first-run",
                "--no-default-browser-check"
            };
            if (_options.DisableSandbox)
            {
                args.Add("--no-sandbox");
                args.Add("--disable-setuid-sandbox");
            }

            _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = true,
                Args = args
            });
            _browser.Disconnected += (_, _) => _logger.LogError("TaskForge browser-api Chromium process disconnected.");
            _logger.LogInformation("TaskForge browser-api Chromium started.");
        }
        finally
        {
            _startLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            if (_browser is not null) await _browser.CloseAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to close Chromium cleanly.");
        }
        finally
        {
            _browser = null;
            _playwright?.Dispose();
            _playwright = null;
            _startLock.Dispose();
        }
    }
}
