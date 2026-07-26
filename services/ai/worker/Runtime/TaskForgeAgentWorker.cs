using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace TaskForge.AiAgent.Runtime;

/// <summary>
/// Compatibility shim for deployments that unpack releases over an older source tree.
/// The former polling worker was removed in favour of <see cref="TaskForgeAgentWakeService"/>.
/// Keeping this harmless class at the old path overwrites stale copies and prevents them
/// from restoring the retired ClaimBatchDelayMs/HeartbeatSeconds polling loop.
/// </summary>
[Obsolete("Use TaskForgeAgentWakeService. The AI worker is now awakened by the backend.")]
public sealed class TaskForgeAgentWorker : BackgroundService
{
    private readonly ILogger<TaskForgeAgentWorker> _logger;

    public TaskForgeAgentWorker(ILogger<TaskForgeAgentWorker> logger)
    {
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogWarning(
            "Legacy TaskForgeAgentWorker was registered. Polling is disabled; use TaskForgeAgentWakeService instead.");
        return Task.CompletedTask;
    }
}
