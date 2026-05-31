namespace TaskForge.Solutions.RatingWorker;

public sealed class Worker(ILogger<Worker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Rating worker started. It must consume SolutionVerdictChanged events and update UserRatings/LeaderboardEntries read models.");
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }
}
