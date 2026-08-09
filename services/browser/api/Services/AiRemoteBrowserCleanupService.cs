namespace TaskForge.Browser.Api.Services;

internal sealed class AiRemoteBrowserCleanupService(
    AiRemoteBrowserService remoteBrowser,
    ILogger<AiRemoteBrowserCleanupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await remoteBrowser.CloseExpiredAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // Cleanup is best-effort and must not permanently stop after one
                    // transient Chromium failure. BrowserSessionRegistry has its own
                    // expiration cleanup as a second safety net.
                    logger.LogWarning(ex, "AI remote-browser cleanup iteration failed; retrying on the next tick.");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
