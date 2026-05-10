using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TaskForge.AiAgent.Contracts;

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
        var workflows = new ITaskForgeWorkflow[]
        {
            _services.GetRequiredService<PolishAssignmentDraftWorkflow>(),
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
}
