using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TaskForge.AiAgent.Contracts;
using TaskForge.AiAgent.Infrastructure;

namespace TaskForge.AiAgent.Runtime;

public sealed class TaskForgeAgentWakeService : BackgroundService
{
    private readonly TaskForgeInternalApiClient _api;
    private readonly TaskForgeAgentRuntime _runtime;
    private readonly ILogger<TaskForgeAgentWakeService> _logger;
    private readonly Channel<WakeSignal> _wakeSignals;
    private readonly string _workerId;

    public TaskForgeAgentWakeService(
        TaskForgeInternalApiClient api,
        TaskForgeAgentRuntime runtime,
        ILogger<TaskForgeAgentWakeService> logger)
    {
        _api = api;
        _runtime = runtime;
        _logger = logger;
        _wakeSignals = Channel.CreateBounded<WakeSignal>(new BoundedChannelOptions(1)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });
        var rawWorkerId = $"agent-{Environment.MachineName}-{Guid.NewGuid():N}";
        _workerId = rawWorkerId.Length <= 48 ? rawWorkerId : rawWorkerId[..48];
    }

    public bool Wake(string reason, Guid? runId = null)
    {
        var cleanReason = string.IsNullOrWhiteSpace(reason) ? "backend-request" : reason.Trim();
        return _wakeSignals.Writer.TryWrite(new WakeSignal(cleanReason, runId));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("TaskForge AI worker push mode started: {WorkerId}", _workerId);
        _wakeSignals.Writer.TryWrite(new WakeSignal("worker-startup", null));

        await foreach (var signal in _wakeSignals.Reader.ReadAllAsync(stoppingToken))
        {
            _logger.LogInformation(
                "AI worker wake received: worker={WorkerId} reason={Reason} runId={RunId}",
                _workerId, signal.Reason, signal.RunId);
            await DrainQueuedJobsAsync(stoppingToken);
        }
    }

    private async Task DrainQueuedJobsAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            ClaimedAgentJob? job;
            try
            {
                job = await _api.ClaimNextAsync(_workerId, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AI worker could not claim a queued job after backend wake.");
                return;
            }

            if (job == null)
            {
                _logger.LogInformation("AI worker queue drained: worker={WorkerId}", _workerId);
                return;
            }

            await _runtime.HandleAsync(job, _workerId, stoppingToken);
        }
    }

    private readonly record struct WakeSignal(string Reason, Guid? RunId);


}
