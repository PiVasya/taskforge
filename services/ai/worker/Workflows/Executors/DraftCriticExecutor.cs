using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Agents.AI;
using TaskForge.AiAgent.Contracts;
using TaskForge.AiAgent.Llm;
using TaskForge.AiAgent.Prompts;
using TaskForge.AiAgent.Runtime;
using TaskForge.AiAgent.Tools;

namespace TaskForge.AiAgent.Workflows.Executors;

public sealed partial class DraftCriticExecutor
{
    private readonly ValidationTools _validationTools;
    private readonly TaskForgeAgentFactory _agentFactory;
    private readonly AgentSessionStore _sessionStore;
    private readonly AgentStepReporter _steps;
    private readonly DirectLlmTextClient _textClient;
    private AIAgent? _agent;

    public DraftCriticExecutor(ValidationTools validationTools, TaskForgeAgentFactory agentFactory, AgentSessionStore sessionStore, AgentStepReporter steps, DirectLlmTextClient textClient)
    {
        _validationTools = validationTools;
        _agentFactory = agentFactory;
        _sessionStore = sessionStore;
        _steps = steps;
        _textClient = textClient;
    }

    public async Task<JsonObject> ExecuteAsync(WorkflowState state, JsonObject validationResult, CancellationToken cancellationToken)
    {
        if (state.Draft == null)
            return new JsonObject { ["isAccepted"] = false, ["issues"] = new JsonArray("draft is null") };

        await _steps.TryReportAsync("critic", "running", "Критик проверяет качество задания", state.Draft.Title);

        var staticCritique = await _validationTools.StaticDraftCritiqueAsync(state.Draft.ToArtifactData());
        var prompt = $$"""
{{TaskForgeAgentPrompts.Critic}}

Пользовательский запрос:
{{state.UserText}}

COURSE_SKILL_MAP / педагогический план вставки:
{{state.CourseSkillBridge?.ToJsonObject().ToJsonString() ?? "{}"}}

Draft:
{{BuildModelCriticDraftPayload(state.Draft).ToJsonString()}}

Validation result:
{{validationResult.ToJsonString()}}

Static critique:
{{staticCritique.ToJsonString()}}

Педагогические предпочтения преподавателя / память агента:
{{state.TeacherPreferences.ToJsonString()}}
""";
        JsonObject modelCritique;
        try
        {
            // The critic is advisory on top of deterministic checks and does not need tools.
            // Use direct chat-completions to avoid agent-session/provider-adapter role conversion failures.
            var responseText = await _textClient.CompleteAsync(prompt, cancellationToken);
            modelCritique = ParseCritique(responseText);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            state.Notes.Add($"Model critic call failed: {ex.GetType().Name}: {ex.Message}. Deterministic validation and bridge/static critique will decide.");
            modelCritique = new JsonObject
            {
                ["isAccepted"] = true,
                ["score"] = 82,
                ["issues"] = new JsonArray($"model critic unavailable: {ex.GetType().Name}: {ex.Message}"),
                ["advisoryOnly"] = true
            };
        }
        var bridgeCritique = EvaluateBridgeConsistency(state);
        var validationOk = IsOk(validationResult);
        var staticOk = IsAccepted(staticCritique);
        var bridgeOk = IsAccepted(bridgeCritique);
        var modelOk = IsAccepted(modelCritique) || IsAdvisoryModelCritique(modelCritique, validationOk, staticOk && bridgeOk);
        var accepted = staticOk && bridgeOk && modelOk && validationOk;
        var combined = new JsonObject
        {
            ["isAccepted"] = accepted,
            ["staticCritique"] = staticCritique.DeepClone(),
            ["bridgeCritique"] = bridgeCritique.DeepClone(),
            ["modelCritique"] = modelCritique.DeepClone(),
            ["validation"] = validationResult.DeepClone()
        };
        state.Data["critique"] = combined.DeepClone();

        await _steps.TryReportAsync("critic", accepted ? "completed" : "failed", accepted ? "Черновик принят критиком" : "Критик нашёл проблемы", null, combined);
        return combined;
    }


}
