using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TaskForge.AiAgent.Infrastructure;
using TaskForge.AiAgent.Options;

namespace TaskForge.AiAgent.Runtime;

public sealed class TaskForgeAgentWorker : BackgroundService
{
    private readonly TaskForgeInternalApiClient _api;
    private readonly TaskForgeAgentRuntime _runtime;
    private readonly TaskForgeInternalApiOptions _apiOptions;
    private readonly ILogger<TaskForgeAgentWorker> _logger;
    private readonly string _workerId;

    public TaskForgeAgentWorker(
        TaskForgeInternalApiClient api,
        TaskForgeAgentRuntime runtime,
        IOptions<TaskForgeInternalApiOptions> apiOptions,
        ILogger<TaskForgeAgentWorker> logger)
    {
        _api = api;
        _runtime = runtime;
        _apiOptions = apiOptions.Value;
        _logger = logger;
        var rawWorkerId = $"dotnet-agent-{Environment.MachineName}-{Guid.NewGuid():N}";
        _workerId = rawWorkerId.Length <= 48 ? rawWorkerId : rawWorkerId[..48];
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("TaskForge .NET AI Agent worker started: {WorkerId}", _workerId);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var job = await _api.ClaimNextAsync(_workerId, stoppingToken);
                if (job == null)
                {
                    await Task.Delay(_apiOptions.ClaimBatchDelayMs, stoppingToken);
                    continue;
                }

                await RunWithHeartbeatAsync(job, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Worker polling loop failed.");
                await Task.Delay(System.Math.Max(1000, _apiOptions.ClaimBatchDelayMs * 2), stoppingToken);
            }
        }
    }

    private async Task RunWithHeartbeatAsync(Contracts.ClaimedAgentJob job, CancellationToken stoppingToken)
    {
        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var heartbeat = Task.Run(async () =>
        {
            while (!runCts.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(_apiOptions.HeartbeatSeconds), runCts.Token);
                    await _api.HeartbeatAsync(job.RunId, _workerId, runCts.Token);
                }
                catch (OperationCanceledException) when (runCts.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Heartbeat failed for run {RunId}", job.RunId);
                }
            }
        }, runCts.Token);

        try
        {
            await _runtime.HandleAsync(job, _workerId, runCts.Token);
        }
        finally
        {
            await runCts.CancelAsync();
            try { await heartbeat; } catch { }
        }
    }
}
