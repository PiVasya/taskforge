using TaskForge.AiAgent.Contracts;
using TaskForge.AiAgent.Runtime;
using TaskForge.AiAgent.Workflows.Executors;

namespace TaskForge.AiAgent.Workflows;

public sealed class CourseAuditWorkflow : ITaskForgeWorkflow
{
    private readonly LoadRunContextExecutor _loadContext;
    private readonly PlanRequestExecutor _planner;
    private readonly CourseAuditExecutor _auditor;
    private readonly ResultEnvelopeBuilder _envelopes;

    public CourseAuditWorkflow(LoadRunContextExecutor loadContext, PlanRequestExecutor planner, CourseAuditExecutor auditor, ResultEnvelopeBuilder envelopes)
    {
        _loadContext = loadContext;
        _planner = planner;
        _auditor = auditor;
        _envelopes = envelopes;
    }

    public string Name => "course_audit";
    public int Priority => 40;

    public bool CanHandle(ClaimedAgentJob job)
    {
        var text = job.UserText.ToLowerInvariant();
        return text.Contains("аудит") || text.Contains("пробел") || text.Contains("дыр") || text.Contains("анализ курса") || text.Contains("gap") || text.Contains("улучшить курс");
    }

    public async Task<AgentResultEnvelope> RunAsync(ClaimedAgentJob job, CancellationToken cancellationToken)
    {
        var state = new WorkflowState { Job = job, WorkflowName = Name, ScenarioId = "course_gap_audit" };
        var context = await _loadContext.ExecuteAsync(state);
        await _planner.ExecuteAsync(state, context, cancellationToken);
        var audit = await _auditor.ExecuteAsync(state, context, cancellationToken);

        state.Artifacts.Add(new AgentArtifact("course_gap_audit", "Аудит курса", audit));
        state.AssistantMessage = audit["summary"]?.ToString() ?? "Я подготовил аудит курса и вынес findings в artifact.";
        return _envelopes.FromWorkflowState(state);
    }
}
