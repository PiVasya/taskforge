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

COURSE_SKILL_MAP / педагогический план вставки:
{{state.CourseSkillBridge?.ToJsonObject().ToJsonString() ?? "{}"}}

Draft:
{{state.Draft.ToArtifactData().ToJsonString()}}

Validation result:
{{validationResult.ToJsonString()}}

Static critique:
{{staticCritique.ToJsonString()}}

Педагогические предпочтения преподавателя / память агента:
{{state.TeacherPreferences.ToJsonString()}}
""";
        var response = await _agent.RunAsync(prompt, session, cancellationToken: cancellationToken);
        await _sessionStore.SaveAsync(_agent, session, state.Job.ConversationId, cancellationToken);

        var modelCritique = ParseCritique(response.Text ?? string.Empty);
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

    private static JsonObject EvaluateBridgeConsistency(WorkflowState state)
    {
        var issues = new JsonArray();
        var draft = state.Draft;
        var bridge = state.CourseSkillBridge;
        if (draft == null || bridge == null || !bridge.IsBridgeRequest)
        {
            return new JsonObject { ["isAccepted"] = true, ["score"] = 100, ["issues"] = issues };
        }

        var hasPlan = bridge.BridgePlan is { Count: > 0 };
        if (!hasPlan)
        {
            issues.Add("learning-bridge requires a COURSE_SKILL_MAP bridgePlan; draft generation must not invent tasks without it");
            return new JsonObject { ["isAccepted"] = false, ["score"] = 20, ["issues"] = issues };
        }

        var stepIndex = ReadInt(draft.Extra["bridgeStepIndex"]) ?? draft.SourceTaskIndex ?? 0;
        JsonObject? plannedStep = null;
        if (stepIndex >= 0 && stepIndex < bridge.BridgePlan!.Count)
            plannedStep = bridge.BridgePlan[stepIndex];
        if (plannedStep == null)
        {
            issues.Add($"draft bridgeStepIndex {stepIndex} does not exist in COURSE_SKILL_MAP bridgePlan");
            return new JsonObject { ["isAccepted"] = false, ["score"] = 20, ["issues"] = issues };
        }

        var introduced = ReadStringArray(draft.Extra["introducedSkills"]).ToList();
        var plannedIntroduced = ReadStringArray(plannedStep["introducedSkills"]).ToList();
        if (introduced.Count == 0)
            issues.Add("learning-bridge draft must declare introducedSkills from its COURSE_SKILL_MAP step");
        if (introduced.Count > 1)
            issues.Add("learning-bridge draft introduces more than one main skill");
        if (plannedIntroduced.Count > 0 && introduced.Count > 0 && !introduced.Any(x => plannedIntroduced.Contains(x, StringComparer.OrdinalIgnoreCase)))
            issues.Add("draft introducedSkills do not match the planned COURSE_SKILL_MAP step");

        var text = $"{draft.Title} {draft.Description} {draft.ReferenceSolution}".ToLowerInvariant();
        foreach (var forbidden in ReadStringArray(plannedStep["mustNotUse"]))
        {
            var needle = NormalizeForLooseContains(forbidden);
            if (needle.Length >= 4 && NormalizeForLooseContains(text).Contains(needle, StringComparison.OrdinalIgnoreCase))
                issues.Add($"draft appears to use future/forbidden skill from mustNotUse: {forbidden}");
        }

        return new JsonObject
        {
            ["isAccepted"] = issues.Count == 0,
            ["score"] = issues.Count == 0 ? 96 : Math.Max(25, 96 - issues.Count * 22),
            ["issues"] = issues,
            ["plannedStep"] = plannedStep.DeepClone()
        };
    }

    private static int? ReadInt(JsonNode? node)
    {
        return int.TryParse(node?.ToString(), out var value) ? value : null;
    }

    private static IEnumerable<string> ReadStringArray(JsonNode? node)
    {
        if (node is JsonArray arr)
        {
            foreach (var item in arr)
            {
                var text = item?.ToString();
                if (!string.IsNullOrWhiteSpace(text)) yield return text.Trim();
            }
        }
        else if (!string.IsNullOrWhiteSpace(node?.ToString()))
        {
            foreach (var item in node!.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                yield return item;
        }
    }

    private static string NormalizeForLooseContains(string? value)
    {
        var text = (value ?? string.Empty).ToLowerInvariant();
        return Regex.Replace(text, @"[^a-zа-я0-9+#<>.]+", " ").Trim();
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
        if (ContainsHardPedagogicalVeto(obj)) return false;

        if (int.TryParse(obj["score"]?.ToString(), out var score) && score >= 70)
            return true;

        return false;
    }


    private static bool ContainsHardPedagogicalVeto(JsonObject obj)
    {
        var text = string.Join(" ", ReadIssueTexts(obj["issues"] as JsonArray)).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(text)) return false;

        return text.Contains("больше одного нового навыка")
               || text.Contains("не соответствует course_skill_map")
               || text.Contains("не соответствует skill")
               || text.Contains("mustnotuse")
               || text.Contains("must not use")
               || text.Contains("слишком слож")
               || text.Contains("резкий скачок")
               || text.Contains("использует будущ")
               || text.Contains("не вход") && text.Contains("bridgeplan");
    }

    private static IEnumerable<string> ReadIssueTexts(JsonArray? arr)
    {
        if (arr == null) yield break;
        foreach (var item in arr)
        {
            var text = item?.ToString();
            if (!string.IsNullOrWhiteSpace(text)) yield return text;
        }
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
