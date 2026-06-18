using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TaskForge.AiAgent.Contracts;
using TaskForge.AiAgent.Infrastructure;
using TaskForge.AiAgent.Observability;
using TaskForge.AiAgent.Options;
using TaskForge.AiAgent.Workflows;

namespace TaskForge.AiAgent.Runtime;

public sealed class TaskForgeAgentRuntime
{
    private readonly TaskForgeWorkflowRouter _router;
    private readonly TaskForgeInternalApiClient _api;
    private readonly AgentRunContextAccessor _contextAccessor;
    private readonly AgentStepReporter _steps;
    private readonly AgentTelemetry _telemetry;
    private readonly TaskForgeAgentOptions _options;
    private readonly ILogger<TaskForgeAgentRuntime> _logger;

    public TaskForgeAgentRuntime(
        TaskForgeWorkflowRouter router,
        TaskForgeInternalApiClient api,
        AgentRunContextAccessor contextAccessor,
        AgentStepReporter steps,
        AgentTelemetry telemetry,
        IOptions<TaskForgeAgentOptions> options,
        ILogger<TaskForgeAgentRuntime> logger)
    {
        _router = router;
        _api = api;
        _contextAccessor = contextAccessor;
        _steps = steps;
        _telemetry = telemetry;
        _options = options.Value;
        _logger = logger;
    }

    public async Task HandleAsync(ClaimedAgentJob job, string workerId, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(System.Math.Max(30, _options.MaxJobSeconds)));

        var context = new AgentRunContext(job, _api, workerId, timeoutCts.Token);
        using var _ = _contextAccessor.Push(context);
        using var measure = _telemetry.Measure("agent-run", job.RunId);

        try
        {
            await _steps.TryReportAsync("runtime", "running", "Ассистент начал обработку", "Готовлю безопасный сценарий работы с курсом.");
            var workflow = _router.Resolve(job);
            await _steps.TryReportAsync("workflow", "running", "Выбран сценарий обработки", "Ассистент будет работать через проверяемые шаги и не внесёт изменения без подтверждения.");
            var result = await workflow.RunAsync(job, timeoutCts.Token);
            result.Debug["provider"] = _options.Provider;
            result.Debug["model"] = _options.Model;
            result.Debug["agentRuntime"] = "dotnet-v2";
            await _api.CompleteAsync(job.RunId, workerId, result, _options.IncludeRawModelTextInDebug, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TaskForge .NET agent run failed. run={RunId}", job.RunId);
            await _api.FailAsync(job.RunId, workerId, ex, cancellationToken);
        }
    }
}
