namespace TaskForge.Browser.Api.Services;

public sealed class BrowserSessionCleanupService(BrowserSessionRegistry sessions, ILogger<BrowserSessionCleanupService> logger) : BackgroundService
{
    private readonly BrowserSessionRegistry _sessions = sessions;
    private readonly ILogger<BrowserSessionCleanupService> _logger = logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var removed = await _sessions.CleanupExpiredAsync();
            if (removed > 0) _logger.LogInformation("Closed {Count} expired browser sessions.", removed);
        }
    }
}
