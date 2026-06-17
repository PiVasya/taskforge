using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TaskForge.AiAgent.Contracts;
using TaskForge.AiAgent.Workflows.AgentLoop;

namespace TaskForge.AiAgent.Workflows;

public sealed class TaskForgeWorkflowRouter
{
    private readonly IServiceProvider _services;
    private readonly ILogger<TaskForgeWorkflowRouter> _logger;

    public TaskForgeWorkflowRouter(IServiceProvider services, ILogger<TaskForgeWorkflowRouter> logger)
    {
        _services = services;
        _logger = logger;
    }

    public ITaskForgeWorkflow Resolve(ClaimedAgentJob job)
    {
        var adaptive = _services.GetService<AdaptiveAgentLoopWorkflow>();
        if (adaptive is not null && adaptive.CanHandle(job))
        {
            _logger.LogInformation("Selected workflow {Workflow} for run {RunId}: adaptive agent loop is enabled", adaptive.Name, job.RunId);
            return adaptive;
        }

        var polish = _services.GetRequiredService<PolishAssignmentDraftWorkflow>();
        if (polish.CanHandle(job))
        {
            _logger.LogInformation("Selected workflow {Workflow} for run {RunId}", polish.Name, job.RunId);
            return polish;
        }

        var intent = AgentIntentClassifier.Select(job);
        var selectedByIntent = ResolveByIntent(intent);
        if (selectedByIntent is not null)
        {
            _logger.LogInformation(
                "Selected workflow {Workflow} for run {RunId} by intent {ScenarioId}: {Reason}",
                selectedByIntent.Name,
                job.RunId,
                intent.ScenarioId,
                intent.Reason);
            return selectedByIntent;
        }

        var workflows = new ITaskForgeWorkflow[]
        {
            _services.GetRequiredService<CourseEditWorkflow>(),
            _services.GetRequiredService<AssignmentDraftWorkflow>(),
            _services.GetRequiredService<CourseAuditWorkflow>(),
            _services.GetRequiredService<OpenChatWorkflow>()
        };

        var selected = workflows
            .Where(x => x.CanHandle(job))
            .OrderByDescending(x => x.Priority)
            .First();

        _logger.LogInformation("Selected workflow {Workflow} for run {RunId}", selected.Name, job.RunId);
        return selected;
    }

    private ITaskForgeWorkflow? ResolveByIntent(AgentIntent intent)
    {
        if (intent.IsCourseEditScenario)
            return _services.GetRequiredService<CourseEditWorkflow>();

        if (intent.IsCourseAuditScenario)
            return _services.GetRequiredService<CourseAuditWorkflow>();

        if (intent.IsDraftScenario)
            return _services.GetRequiredService<AssignmentDraftWorkflow>();

        if (intent.ScenarioId == "free_chat")
            return _services.GetRequiredService<OpenChatWorkflow>();

        return null;
    }
}
