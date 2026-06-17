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
        var intent = AgentIntentClassifier.Select(job);
        if (intent.IsCourseAuditScenario)
            return true;

        var text = job.UserText.ToLowerInvariant();
        return text.Contains("аудит")
               || text.Contains("пробел")
               || text.Contains("скач")
               || text.Contains("не хватает")
               || text.Contains("слаб")
               || text.Contains("застр")
               || text.Contains("анализ курса")
               || text.Contains("изучи курс")
               || text.Contains("разбери курс")
               || text.Contains("посмотри курс")
               || text.Contains("пойми курс")
               || text.Contains("проверь курс")
               || text.Contains("структур")
               || text.Contains("карта курса")
               || text.Contains("gap")
               || text.Contains("улучшить курс");
    }

    public async Task<AgentResultEnvelope> RunAsync(ClaimedAgentJob job, CancellationToken cancellationToken)
    {
        var intent = AgentIntentClassifier.Select(job);
        var scenarioId = intent.IsCourseAuditScenario ? intent.ScenarioId : "course_gap_audit";
        var artifactType = scenarioId == "course_analysis" ? "course_analysis_report" : "course_gap_audit";
        var artifactTitle = scenarioId == "course_analysis" ? "Анализ курса" : "Аудит курса";

        var state = new WorkflowState { Job = job, WorkflowName = Name, ScenarioId = scenarioId };
        state.Data["agentIntent"] = intent.ToJsonObject();
        var context = await _loadContext.ExecuteAsync(state);
        await _planner.ExecuteAsync(state, context, cancellationToken);
        var audit = await _auditor.ExecuteAsync(state, context, cancellationToken);

        audit["scenarioId"] = scenarioId;
        if (intent.SecondaryScenarioId is not null)
            audit["secondaryScenarioId"] = intent.SecondaryScenarioId;
        if (intent.TargetConcept is not null)
            audit["targetConcept"] = intent.TargetConcept;

        state.Artifacts.Add(new AgentArtifact(artifactType, artifactTitle, audit));
        state.AssistantMessage = audit["summary"]?.ToString() ?? (scenarioId == "course_analysis"
            ? "Я подготовил анализ курса и вынес выводы в отдельный материал."
            : "Я подготовил аудит курса и вынес найденные проблемы в отдельный материал.");
        return _envelopes.FromWorkflowState(state);
    }
}
