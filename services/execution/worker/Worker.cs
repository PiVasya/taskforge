namespace TaskForge.Execution.Worker;

public sealed class Worker(ILogger<Worker> logger, IHttpClientFactory httpClientFactory, IConfiguration configuration) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Execution worker started. It must consume ExecutionRequested events and dispatch work to language runners.");
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }
}
