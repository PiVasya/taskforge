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
        var intent = AgentIntentClassifier.Select(job);
        if (intent.IsDraftScenario)
            return true;

        var text = job.UserText.ToLowerInvariant();
        return text.Contains("создай") && (text.Contains("задание") || text.Contains("задач"))
               || text.Contains("сгенерируй") && (text.Contains("задание") || text.Contains("задач"))
               || text.Contains("придумай") && (text.Contains("задание") || text.Contains("задач"))
               || text.Contains("подготовь") && (text.Contains("задание") || text.Contains("задач") || text.Contains("черновик"))
               || text.Contains("накидай") && (text.Contains("задание") || text.Contains("задач"))
               || text.Contains("черновик")
               || text.Contains("лесен")
               || text.Contains("лестниц")
               || text.Contains("пошаг")
               || text.Contains("микро")
               || text.Contains("ступен")
               || text.Contains("обучал")
               || text.Contains("в стиле курса")
               || text.Contains("как в курсе")
               || text.Contains("похож")
               || text.Contains("как текущ")
               || text.Contains("мостик")
               || text.Contains("между заданиями")
               || text.Contains("между темами")
               || text.Contains("bridge task")
               || text.Contains("guided ladder");
    }

    public async Task<AgentResultEnvelope> RunAsync(ClaimedAgentJob job, CancellationToken cancellationToken)
    {
        var intent = AgentIntentClassifier.Select(job);
        var scenarioId = intent.IsDraftScenario ? intent.ScenarioId : "style_matched_tasks";
        var state = new WorkflowState { Job = job, WorkflowName = Name, ScenarioId = scenarioId };
        state.Data["agentIntent"] = intent.ToJsonObject();
        var context = await _loadContext.ExecuteAsync(state);
        await _preferences.ExecuteAsync(state, context, cancellationToken);
        var plan = await _planner.ExecuteAsync(state, context, cancellationToken);
        await _skillMap.ExecuteAsync(state, context, cancellationToken);

        if (NeedsCourseSkillMapBeforeDrafting(state))
        {
            state.AssistantMessage = "Я не стал сохранять черновики: ассистент не смог достаточно уверенно построить карту навыков курса и точку вставки. Лучше остановиться, чем создать задания не в то место.";
            state.RequiresApproval = false;
            return _envelopes.FromWorkflowState(state);
        }

        var requestedCount = ResolveRequestedDraftCount(intent, job.UserText);
        var shouldRunMany = requestedCount > 1
                            || DraftAuthorExecutor.LooksLikeMultipleDraftRequest(job.UserText)
                            || scenarioId is "guided_ladder" or "style_matched_tasks" or "bridge_tasks" or "draft_revision";

        if (shouldRunMany)
        {
            await RunMultiDraftAsync(state, context, plan, requestedCount, cancellationToken);
        }
        else
        {
            await RunSingleDraftAsync(state, context, plan, cancellationToken);
        }

        var draftCount = state.Artifacts.Count(a => string.Equals(a.Type, "assignment_draft_ready", StringComparison.OrdinalIgnoreCase));
        if (draftCount == 0 && state.Data["draftGenerationError"] is JsonObject generationError)
        {
            state.AssistantMessage = $"Я не сохранил черновики: генератор не получил пригодный ответ от модели ({generationError["type"]?.ToString() ?? "DraftGenerationError"}). Невалидный fallback не сохраняю.";
        }
        else
        {
            state.AssistantMessage = draftCount == 0
                ? "Я не сохранил черновики: все подготовленные варианты были отклонены проверками качества."
                : draftCount == 1
                    ? "Я подготовил скрытый черновик задания и прогнал проверки качества. Он появится в курсе как скрытый черновик; перед публикацией его нужно вручную проверить."
                    : $"Я подготовил {draftCount} скрытых черновиков заданий, расставил их по порядку и прогнал проверки качества. Они появятся в курсе как скрытые черновики; перед публикацией их нужно вручную проверить.";
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

    private int ResolveRequestedDraftCount(AgentIntent intent, string userText)
    {
        if (intent.RequestedCount is { } explicitCount)
            return Math.Clamp(explicitCount, 1, _options.MaxDraftsPerRun);

        if (DraftAuthorExecutor.LooksLikeMultipleDraftRequest(userText))
            return 5;

        return intent.ScenarioId switch
        {
            "guided_ladder" => Math.Min(5, _options.MaxDraftsPerRun),
            "bridge_tasks" => Math.Min(4, _options.MaxDraftsPerRun),
            "style_matched_tasks" => Math.Min(3, _options.MaxDraftsPerRun),
            "draft_revision" => Math.Min(3, _options.MaxDraftsPerRun),
            _ => 1
        };
    }

    private async Task RunSingleDraftAsync(WorkflowState state, string context, string plan, CancellationToken cancellationToken)
    {
        DraftSpec? acceptedDraft = null;
        for (var attempt = 0; attempt <= _options.MaxDraftRepairAttempts; attempt++)
        {
            var draft = await _author.ExecuteAsync(state, context, plan, attempt, cancellationToken);
            if (draft == null)
            {
                state.Notes.Add($"Draft attempt {attempt + 1} produced no usable draft; nothing will be saved for this attempt.");
                break;
            }

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

        if (acceptedDraft == null)
        {
            state.Data["draftGenerationError"] ??= new JsonObject
            {
                ["type"] = "DraftRejected",
                ["message"] = "No single draft passed validation and critique; rejected drafts are not saved as hidden course drafts."
            };
            return;
        }

        await _approval.ExecuteHiddenDraftArtifactAsync(state, acceptedDraft);
    }

    private async Task RunMultiDraftAsync(WorkflowState state, string context, string plan, int requestedCount, CancellationToken cancellationToken)
    {
        List<DraftSpec> drafts;
        try
        {
            drafts = await _author.ExecuteManyAsync(state, context, plan, 0, requestedCount, cancellationToken);
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
            var current = draft;
            var saved = false;

            for (var repairAttempt = 0; repairAttempt <= _options.MaxDraftRepairAttempts; repairAttempt++)
            {
                state.Draft = current;
                var validation = await _validator.ExecuteAsync(state, current);
                var critique = await _critic.ExecuteAsync(state, validation, cancellationToken);
                var ok = critique["isAccepted"]?.ToString().Equals("true", StringComparison.OrdinalIgnoreCase) == true;
                if (ok)
                {
                    accepted.Add(current);
                    await _approval.ExecuteHiddenDraftArtifactAsync(state, current);
                    saved = true;
                    break;
                }

                var compactCritique = CompactCritiqueForRepair(critique);
                if (repairAttempt >= _options.MaxDraftRepairAttempts)
                {
                    state.Notes.Add($"Draft '{current.Title}' rejected after {repairAttempt + 1} validation attempt(s) and was not saved: {compactCritique}");
                    break;
                }

                var repaired = await _author.RepairDraftAsync(state, current, validation, critique, repairAttempt + 1, cancellationToken);
                if (repaired == null)
                {
                    state.Notes.Add($"Draft '{current.Title}' rejected and repair failed; it was not saved: {compactCritique}");
                    break;
                }

                current = repaired;
            }

            if (!saved)
                state.Draft = accepted.FirstOrDefault();
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
