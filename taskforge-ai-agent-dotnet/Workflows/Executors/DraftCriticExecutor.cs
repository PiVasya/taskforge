using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Agents.AI;
using TaskForge.AiAgent.Llm;
using TaskForge.AiAgent.Prompts;
using TaskForge.AiAgent.Runtime;
using TaskForge.AiAgent.Tools;

namespace TaskForge.AiAgent.Workflows.Executors;

public sealed class DraftCriticExecutor
{
    private readonly ValidationTools _validationTools;
    private readonly TaskForgeAgentFactory _agentFactory;
    private readonly AgentSessionStore _sessionStore;
    private readonly AgentStepReporter _steps;
    private AIAgent? _agent;

    public DraftCriticExecutor(ValidationTools validationTools, TaskForgeAgentFactory agentFactory, AgentSessionStore sessionStore, AgentStepReporter steps)
    {
        _validationTools = validationTools;
        _agentFactory = agentFactory;
        _sessionStore = sessionStore;
        _steps = steps;
    }

    public async Task<JsonObject> ExecuteAsync(WorkflowState state, JsonObject validationResult, CancellationToken cancellationToken)
    {
        if (state.Draft == null)
            return new JsonObject { ["isAccepted"] = false, ["issues"] = new JsonArray("draft is null") };

        await _steps.TryReportAsync("critic", "running", "Критик проверяет качество задания", state.Draft.Title);

        var staticCritique = await _validationTools.StaticDraftCritiqueAsync(state.Draft.ToArtifactData());
        _agent ??= _agentFactory.CreateCoordinatorAgent();
        var session = await _sessionStore.LoadAsync(_agent, state.Job.ConversationId, cancellationToken);
        var prompt = $$"""
{{TaskForgeAgentPrompts.Critic}}

Пользовательский запрос:
{{state.UserText}}

Draft:
{{state.Draft.ToArtifactData().ToJsonString()}}

Validation result:
{{validationResult.ToJsonString()}}

Static critique:
{{staticCritique.ToJsonString()}}
""";
        var response = await _agent.RunAsync(prompt, session, cancellationToken: cancellationToken);
        await _sessionStore.SaveAsync(_agent, session, state.Job.ConversationId, cancellationToken);

        var modelCritique = ParseCritique(response.Text ?? string.Empty);
        var validationOk = IsOk(validationResult);
        var staticOk = IsAccepted(staticCritique);
        var modelOk = IsAccepted(modelCritique) || IsAdvisoryModelCritique(modelCritique, validationOk, staticOk);
        var accepted = staticOk && modelOk && validationOk;
        var combined = new JsonObject
        {
            ["isAccepted"] = accepted,
            ["staticCritique"] = staticCritique.DeepClone(),
            ["modelCritique"] = modelCritique.DeepClone(),
            ["validation"] = validationResult.DeepClone()
        };
        state.Data["critique"] = combined.DeepClone();

        await _steps.TryReportAsync("critic", accepted ? "completed" : "failed", accepted ? "Черновик принят критиком" : "Критик нашёл проблемы", null, combined);
        return combined;
    }

    private static JsonObject ParseCritique(string text)
    {
        try
        {
            var start = text.IndexOf('{');
            var end = text.LastIndexOf('}');
            if (start >= 0 && end > start && JsonNode.Parse(text[start..(end + 1)]) is JsonObject obj)
                return obj;
        }
        catch
        {
        }
        var looseAccepted = Regex.IsMatch(text, "\"isAccepted\"\\s*:\\s*true", RegexOptions.IgnoreCase)
                            || Regex.IsMatch(text, "\\bisAccepted\\s*[:=]\\s*true", RegexOptions.IgnoreCase);
        var score = 50;
        var scoreMatch = Regex.Match(text, "\"?score\"?\\s*[:=]\\s*(\\d{1,3})", RegexOptions.IgnoreCase);
        if (scoreMatch.Success && int.TryParse(scoreMatch.Groups[1].Value, out var parsedScore))
            score = Math.Clamp(parsedScore, 0, 100);

        return new JsonObject
        {
            ["isAccepted"] = looseAccepted || score >= 80,
            ["score"] = score,
            ["issues"] = new JsonArray(looseAccepted || score >= 80
                ? "critic response was not strict JSON, but contained an accepted verdict"
                : "critic response was not valid JSON"),
            ["raw"] = text.Length > 4000 ? text[..4000] : text
        };
    }


    private static bool IsAdvisoryModelCritique(JsonObject obj, bool validationOk, bool staticOk)
    {
        if (!validationOk || !staticOk) return false;

        // The model critic is useful for hints, but it should not veto a draft that
        // already passed deterministic shape checks, runner tests and static
        // student-facing checks unless it is clearly low quality. This avoids the
        // "0 hidden drafts" failure mode caused by over-strict or stale model advice.
        if (int.TryParse(obj["score"]?.ToString(), out var score) && score >= 70)
            return true;

        return false;
    }

    private static bool IsAccepted(JsonObject obj)
    {
        var accepted = obj["isAccepted"]?.ToString();
        if (string.Equals(accepted, "true", StringComparison.OrdinalIgnoreCase)) return true;
        if (int.TryParse(obj["score"]?.ToString(), out var score) && score >= 80) return true;
        return false;
    }

    private static bool IsOk(JsonObject obj)
    {
        var value = obj["ok"]?.ToString();
        return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }
}
