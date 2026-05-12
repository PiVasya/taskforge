using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using TaskForge.AiAgent.Contracts;
using TaskForge.AiAgent.Options;
using TaskForge.AiAgent.Runtime;
using TaskForge.AiAgent.Workflows.Executors;

namespace TaskForge.AiAgent.Workflows;

public sealed class AssignmentDraftWorkflow : ITaskForgeWorkflow
{
    private readonly LoadRunContextExecutor _loadContext;
    private readonly PlanRequestExecutor _planner;
    private readonly DraftAuthorExecutor _author;
    private readonly DraftValidationExecutor _validator;
    private readonly DraftCriticExecutor _critic;
    private readonly ApprovalGateExecutor _approval;
    private readonly ResultEnvelopeBuilder _envelopes;
    private readonly TaskForgeAgentOptions _options;

    public AssignmentDraftWorkflow(
        LoadRunContextExecutor loadContext,
        PlanRequestExecutor planner,
        DraftAuthorExecutor author,
        DraftValidationExecutor validator,
        DraftCriticExecutor critic,
        ApprovalGateExecutor approval,
        ResultEnvelopeBuilder envelopes,
        IOptions<TaskForgeAgentOptions> options)
    {
        _loadContext = loadContext;
        _planner = planner;
        _author = author;
        _validator = validator;
        _critic = critic;
        _approval = approval;
        _envelopes = envelopes;
        _options = options.Value;
    }

    public string Name => "assignment_draft_workflow";
    public int Priority => 60;

    public bool CanHandle(ClaimedAgentJob job)
    {
        var text = job.UserText.ToLowerInvariant();
        return text.Contains("создай") && (text.Contains("задание") || text.Contains("задач"))
               || text.Contains("сгенерируй") && (text.Contains("задание") || text.Contains("задач"))
               || text.Contains("черновик")
               || text.Contains("лестниц")
               || text.Contains("bridge task")
               || text.Contains("guided ladder");
    }

    public async Task<AgentResultEnvelope> RunAsync(ClaimedAgentJob job, CancellationToken cancellationToken)
    {
        var state = new WorkflowState { Job = job, WorkflowName = Name, ScenarioId = "assignment_draft_workflow" };
        var context = await _loadContext.ExecuteAsync(state);
        var plan = await _planner.ExecuteAsync(state, context, cancellationToken);

        DraftSpec? acceptedDraft = null;
        for (var attempt = 0; attempt <= _options.MaxDraftRepairAttempts; attempt++)
        {
            var draft = await _author.ExecuteAsync(state, context, plan, attempt, cancellationToken);
            var validation = await _validator.ExecuteAsync(state, draft);
            var critique = await _critic.ExecuteAsync(state, validation, cancellationToken);

            if (critique["isAccepted"]?.ToString().Equals("true", StringComparison.OrdinalIgnoreCase) == true)
            {
                acceptedDraft = draft;
                break;
            }

            plan += $"\nRepair attempt {attempt + 1}: учти только эти замечания: {CompactCritiqueForRepair(critique)}";
            state.Notes.Add($"Draft attempt {attempt + 1} rejected by critique.");
        }

        acceptedDraft ??= state.Draft ?? new DraftSpec
        {
            Title = "AI-черновик задания",
            Description = "Черновик был подготовлен, но требует ручной доработки.",
            CourseId = job.CourseId,
            Tags = new List<string> { "AI", "needs-review" }
        };

        await _approval.ExecuteForDraftAsync(state, acceptedDraft);
        state.AssistantMessage = "Я подготовил черновик задания, прогнал проверки качества и вынес результат в artifact. Для сохранения/публикации требуется подтверждение.";
        return _envelopes.FromWorkflowState(state);
    }

    private static string CompactCritiqueForRepair(JsonObject critique)
    {
        var issues = new List<string>();
        AddArrayItems(issues, critique["staticCritique"]?["issues"] as JsonArray);
        AddArrayItems(issues, critique["modelCritique"]?["issues"] as JsonArray);
        AddArrayItems(issues, critique["modelCritique"]?["repairHints"] as JsonArray);
        AddArrayItems(issues, critique["validation"]?["shape"]?["issues"] as JsonArray);

        if (issues.Count == 0)
            return "проверь форматы ввода/вывода, тесты и соответствие запросу пользователя.";

        var compact = string.Join("; ", issues.Select(x => x.Length <= 240 ? x : x[..240] + "..."));
        return compact.Length <= 2000 ? compact : compact[..2000] + "...";
    }

    private static void AddArrayItems(List<string> target, JsonArray? items)
    {
        if (items == null) return;
        foreach (var item in items)
        {
            var text = item?.ToString();
            if (!string.IsNullOrWhiteSpace(text)) target.Add(text);
        }
    }
}
