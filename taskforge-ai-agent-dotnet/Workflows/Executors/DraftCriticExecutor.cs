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
        var accepted = IsAccepted(staticCritique) && IsAccepted(modelCritique) && IsOk(validationResult);
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

        var recovered = RecoverCritiqueFromLooseJson(text);
        if (recovered != null)
            return recovered;

        return new JsonObject
        {
            ["isAccepted"] = false,
            ["score"] = 50,
            ["issues"] = new JsonArray("critic response was not valid JSON"),
            ["raw"] = text.Length > 4000 ? text[..4000] : text
        };
    }

    private static JsonObject? RecoverCritiqueFromLooseJson(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var acceptedMatch = Regex.Match(text, @"""isAccepted""\s*:\s*(true|false)", RegexOptions.IgnoreCase);
        var scoreMatch = Regex.Match(text, @"""score""\s*:\s*(\d{1,3})", RegexOptions.IgnoreCase);

        if (!acceptedMatch.Success && !scoreMatch.Success)
            return null;

        var hasAccepted = acceptedMatch.Success;
        var accepted = hasAccepted && string.Equals(acceptedMatch.Groups[1].Value, "true", StringComparison.OrdinalIgnoreCase);
        var score = scoreMatch.Success && int.TryParse(scoreMatch.Groups[1].Value, out var parsedScore)
            ? Math.Clamp(parsedScore, 0, 100)
            : (accepted ? 80 : 50);

        // A single malformed item inside issues must not reject an otherwise good draft.
        // Keep the raw answer for debug, but preserve the model's verdict/score so the
        // workflow can continue when validation and static critique are green.
        return new JsonObject
        {
            ["isAccepted"] = accepted || (!hasAccepted && score >= 80),
            ["score"] = score,
            ["issues"] = new JsonArray("critic response was loose JSON; verdict recovered from isAccepted/score"),
            ["raw"] = text.Length > 4000 ? text[..4000] : text
        };
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
