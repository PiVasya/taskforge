using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TaskForge.AiAgent.Contracts;
using TaskForge.AiAgent.Infrastructure;

namespace TaskForge.AiAgent.Runtime;

public sealed class TaskForgeAgentWakeService : BackgroundService
{
    private readonly TaskForgeInternalApiClient _api;
    private readonly ILogger<TaskForgeAgentWakeService> _logger;
    private readonly Channel<WakeSignal> _wakeSignals;
    private readonly string _workerId;

    public TaskForgeAgentWakeService(
        TaskForgeInternalApiClient api,
        ILogger<TaskForgeAgentWakeService> logger)
    {
        _api = api;
        _logger = logger;
        _wakeSignals = Channel.CreateBounded<WakeSignal>(new BoundedChannelOptions(1)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });
        var rawWorkerId = $"stub-agent-{Environment.MachineName}-{Guid.NewGuid():N}";
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

            await CompleteStubJobAsync(job, stoppingToken);
        }
    }

    private readonly record struct WakeSignal(string Reason, Guid? RunId);

    private async Task CompleteStubJobAsync(ClaimedAgentJob job, CancellationToken cancellationToken)
    {
        try
        {
            await _api.AppendStepAsync(job.RunId, _workerId, new AgentStepPayload
            {
                Kind = "worker",
                Status = "completed",
                ActionName = "stub_processing",
                Title = "Задача обработана",
                Summary = "AI worker получил задачу от backend и завершил временную тестовую обработку.",
                Data = new JsonObject
                {
                    ["stub"] = true,
                    ["jobType"] = job.JobType,
                    ["receivedAtUtc"] = DateTimeOffset.UtcNow.ToString("O")
                }
            }, cancellationToken);

            var result = new AgentResultEnvelope
            {
                Status = "completed",
                ScenarioId = "temporary_stub",
                AssistantMessage = "AI worker работает: задача получена и успешно обработана временной заглушкой.",
                MemoryPatch = new JsonObject
                {
                    ["lastRunId"] = job.RunId.ToString(),
                    ["lastIntent"] = "temporary_stub",
                    ["stubCompletedAtUtc"] = DateTimeOffset.UtcNow.ToString("O")
                }
            };
            result.Debug["stub"] = true;
            result.Debug["workerId"] = _workerId;

            await _api.CompleteAsync(job.RunId, _workerId, result, includeDebug: true, cancellationToken);
            _logger.LogInformation("AI worker stub completed run: runId={RunId} conversationId={ConversationId}", job.RunId, job.ConversationId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AI worker stub failed run {RunId}", job.RunId);
            try
            {
                await _api.FailAsync(job.RunId, _workerId, ex, cancellationToken);
            }
            catch (Exception failEx)
            {
                _logger.LogError(failEx, "AI worker could not report failure for run {RunId}", job.RunId);
            }
        }
    }
}
