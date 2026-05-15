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
    private readonly TeacherPreferenceExecutor _preferences;
    private readonly CourseSkillMapExecutor _skillMap;
    private readonly DraftAuthorExecutor _author;
    private readonly DraftValidationExecutor _validator;
    private readonly DraftCriticExecutor _critic;
    private readonly ApprovalGateExecutor _approval;
    private readonly ResultEnvelopeBuilder _envelopes;
    private readonly TaskForgeAgentOptions _options;

    public AssignmentDraftWorkflow(
        LoadRunContextExecutor loadContext,
        PlanRequestExecutor planner,
        TeacherPreferenceExecutor preferences,
        CourseSkillMapExecutor skillMap,
        DraftAuthorExecutor author,
        DraftValidationExecutor validator,
        DraftCriticExecutor critic,
        ApprovalGateExecutor approval,
        ResultEnvelopeBuilder envelopes,
        IOptions<TaskForgeAgentOptions> options)
    {
        _loadContext = loadContext;
        _planner = planner;
        _preferences = preferences;
        _skillMap = skillMap;
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
        await _preferences.ExecuteAsync(state, context, cancellationToken);
        var plan = await _planner.ExecuteAsync(state, context, cancellationToken);
        await _skillMap.ExecuteAsync(state, context, cancellationToken);

        if (NeedsCourseSkillMapBeforeDrafting(state))
        {
            state.AssistantMessage = "Я не стал сохранять AI-черновики: агент не смог достаточно уверенно построить карту навыков курса и точку вставки. Это лучше, чем снова сгенерировать задания не туда. Подробности есть в артефакте карты навыков и AI dump/logs.";
            state.RequiresApproval = false;
            return _envelopes.FromWorkflowState(state);
        }

        if (DraftAuthorExecutor.LooksLikeMultipleDraftRequest(job.UserText))
        {
            await RunMultiDraftAsync(state, context, plan, cancellationToken);
        }
        else
        {
            await RunSingleDraftAsync(state, context, plan, cancellationToken);
        }

        var draftCount = state.Artifacts.Count(a => string.Equals(a.Type, "assignment_draft_ready", StringComparison.OrdinalIgnoreCase));
        if (draftCount == 0 && state.Data["draftGenerationError"] is JsonObject generationError)
        {
            state.AssistantMessage = $"Я не сохранил AI-черновики: генератор черновиков не получил пригодный ответ от LLM ({generationError["type"]?.ToString() ?? "DraftGenerationError"}). Невалидный fallback больше не сохраняю; подробности есть в AI dump/logs.";
        }
        else
        {
            state.AssistantMessage = draftCount == 0
                ? "Я не сохранил AI-черновики: все подготовленные варианты были отклонены проверками качества. Подробности есть в AI dump/logs."
                : draftCount == 1
                    ? "Я подготовил скрытый AI-черновик задания и прогнал проверки качества. Он появится в курсе как скрытый черновик; перед публикацией его нужно вручную проверить."
                    : $"Я подготовил {draftCount} скрытых AI-черновиков заданий, расставил их по порядку и прогнал проверки качества. Они появятся в курсе как скрытые черновики; перед публикацией их нужно вручную проверить.";
        }
        state.RequiresApproval = false;
        return _envelopes.FromWorkflowState(state);
    }


    private static bool NeedsCourseSkillMapBeforeDrafting(WorkflowState state)
    {
        var bridge = state.CourseSkillBridge;
        if (bridge == null) return false;
        if (!bridge.IsBridgeRequest) return false;
        var hasLlmMap = string.Equals(bridge.Source, "llm-course-skill-map", StringComparison.OrdinalIgnoreCase);
        var hasBridgePlan = bridge.BridgePlan is { Count: > 0 };
        var hasAnchor = bridge.BeforeAssignmentId.HasValue;
        return !hasLlmMap || !hasBridgePlan || !hasAnchor;
    }

    private async Task RunSingleDraftAsync(WorkflowState state, string context, string plan, CancellationToken cancellationToken)
    {
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
            CourseId = state.Job.CourseId,
            Tags = new List<string> { "AI", "needs-review" }
        };

        await _approval.ExecuteHiddenDraftArtifactAsync(state, acceptedDraft);
    }

    private async Task RunMultiDraftAsync(WorkflowState state, string context, string plan, CancellationToken cancellationToken)
    {
        List<DraftSpec> drafts;
        try
        {
            drafts = await _author.ExecuteManyAsync(state, context, plan, 0, 5, cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            state.Notes.Add($"Draft generation failed before validation: {ex.GetType().Name}: {ex.Message}");
            state.Data["draftGenerationError"] = new JsonObject
            {
                ["type"] = ex.GetType().Name,
                ["message"] = ex.Message
            };
            return;
        }

        var accepted = new List<DraftSpec>();

        foreach (var draft in drafts)
        {
            state.Draft = draft;
            var validation = await _validator.ExecuteAsync(state, draft);
            var critique = await _critic.ExecuteAsync(state, validation, cancellationToken);
            var ok = critique["isAccepted"]?.ToString().Equals("true", StringComparison.OrdinalIgnoreCase) == true;
            if (!ok)
            {
                state.Notes.Add($"Draft '{draft.Title}' rejected and was not saved as hidden draft: {CompactCritiqueForRepair(critique)}");
                continue;
            }

            accepted.Add(draft);
            await _approval.ExecuteHiddenDraftArtifactAsync(state, draft);
        }

        state.Draft = accepted.FirstOrDefault();
    }

    private static string CompactCritiqueForRepair(JsonObject critique)
    {
        var issues = new List<string>();
        AddArrayItems(issues, critique["staticCritique"]?["issues"] as JsonArray);
        AddArrayItems(issues, critique["bridgeCritique"]?["blockingIssues"] as JsonArray);
        AddArrayItems(issues, critique["bridgeCritique"]?["advisoryIssues"] as JsonArray);
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
