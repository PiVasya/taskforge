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
        JsonObject modelCritique;
        try
        {
            var response = await _agent.RunAsync(prompt, session, cancellationToken: cancellationToken);
            await _sessionStore.SaveAsync(_agent, session, state.Job.ConversationId, cancellationToken);
            modelCritique = ParseCritique(response.Text ?? string.Empty);
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

    private static JsonObject EvaluateBridgeConsistency(WorkflowState state)
    {
        var blocking = new JsonArray();
        var advisory = new JsonArray();
        var draft = state.Draft;
        var bridge = state.CourseSkillBridge;
        if (draft == null || bridge == null || !bridge.IsBridgeRequest)
        {
            return new JsonObject { ["isAccepted"] = true, ["score"] = 100, ["issues"] = blocking, ["advisoryIssues"] = advisory };
        }

        var hasPlan = bridge.BridgePlan is { Count: > 0 };
        if (!hasPlan)
        {
            blocking.Add("learning-bridge requires a COURSE_SKILL_MAP bridgePlan; draft generation must not invent tasks without it");
            return new JsonObject { ["isAccepted"] = false, ["score"] = 20, ["issues"] = blocking, ["blockingIssues"] = blocking.DeepClone(), ["advisoryIssues"] = advisory };
        }

        var stepIndex = NormalizeBridgeStepIndex(ReadInt(draft.Extra["bridgeStepIndex"]) ?? draft.SourceTaskIndex ?? 0, bridge.BridgePlan!.Count);
        JsonObject? plannedStep = null;
        if (stepIndex >= 0 && stepIndex < bridge.BridgePlan!.Count)
            plannedStep = bridge.BridgePlan[stepIndex];
        if (plannedStep == null)
        {
            blocking.Add($"draft bridgeStepIndex {stepIndex} does not exist in COURSE_SKILL_MAP bridgePlan");
            return new JsonObject { ["isAccepted"] = false, ["score"] = 20, ["issues"] = blocking, ["blockingIssues"] = blocking.DeepClone(), ["advisoryIssues"] = advisory };
        }

        var introduced = ReadStringArray(draft.Extra["introducedSkills"]).ToList();
        var modelIntroduced = ReadStringArray(draft.Extra["modelIntroducedSkills"]).ToList();
        var plannedIntroduced = ReadStringArray(plannedStep["introducedSkills"]).ToList();
        var plannedSkillIds = BuildSkillIdSet(
            ReadStringArray(plannedStep["introducedSkillIds"])
                .Concat(ReadStringArray(plannedStep["skillId"]))
                .Concat(plannedIntroduced)
                .Concat(ReadStringArray(plannedStep["titleHint"]))
                .Concat(ReadStringArray(plannedStep["reason"])));

        var draftSkillIds = BuildSkillIdSet(
            ReadStringArray(draft.Extra["introducedSkillIds"])
                .Concat(ReadStringArray(draft.Extra["bridgeSkillId"]))
                .Concat(introduced)
                .Concat(modelIntroduced)
                .Concat(CourseSkillAnalyzer.DetectCanonicalSkillIds($"{draft.Title} {draft.Description} {draft.ReferenceSolution}")));

        if (introduced.Count == 0 && modelIntroduced.Count == 0 && draftSkillIds.Count == 0)
            blocking.Add("learning-bridge draft must declare or clearly demonstrate introducedSkills from its COURSE_SKILL_MAP step");

        var allowedSkillIds = BuildAllowedSkillIds(bridge, plannedStep, plannedSkillIds);
        var declaredSkillIds = BuildSkillIdSet(
            ReadStringArray(draft.Extra["introducedSkillIds"])
                .Concat(ReadStringArray(draft.Extra["bridgeSkillId"]))
                .Concat(introduced)
                .Concat(modelIntroduced));
        var inferredExtraSkillIds = draftSkillIds
            .Where(id => !plannedSkillIds.Contains(id) && !allowedSkillIds.Contains(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var declaredExtraSkillIds = declaredSkillIds
            .Where(id => !plannedSkillIds.Contains(id) && !allowedSkillIds.Contains(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (declaredExtraSkillIds.Count > 0)
        {
            blocking.Add($"learning-bridge draft declares extra future skills beyond the planned step: {string.Join(", ", declaredExtraSkillIds)}");
        }
        else if (inferredExtraSkillIds.Any(IsHardFutureSkill))
        {
            blocking.Add($"learning-bridge draft appears to use hard future skills beyond the planned step: {string.Join(", ", inferredExtraSkillIds.Where(IsHardFutureSkill))}");
        }
        else if (inferredExtraSkillIds.Count > 0)
        {
            advisory.Add($"draft uses implementation/support skills in addition to the planned step: {string.Join(", ", inferredExtraSkillIds)}");
        }

        if (plannedSkillIds.Count > 0 && declaredSkillIds.Count > 0 && !declaredSkillIds.Overlaps(plannedSkillIds) && !declaredSkillIds.All(allowedSkillIds.Contains))
            blocking.Add($"declared draft skill ids [{string.Join(", ", declaredSkillIds)}] do not match planned skill ids [{string.Join(", ", plannedSkillIds)}]");
        else if (plannedSkillIds.Count > 0 && draftSkillIds.Count > 0 && !draftSkillIds.Overlaps(plannedSkillIds))
            advisory.Add($"inferred draft skill ids [{string.Join(", ", draftSkillIds)}] do not directly mention planned skill ids [{string.Join(", ", plannedSkillIds)}]");

        var usedSkillIds = BuildSkillIdSet(CourseSkillAnalyzer.DetectCanonicalSkillIds($"{draft.Title} {draft.Description} {draft.ReferenceSolution}"));
        var normalizedText = NormalizeForLooseContains($"{draft.Title} {draft.Description} {draft.ReferenceSolution}");
        foreach (var forbidden in ReadStringArray(plannedStep["mustNotUse"]))
        {
            if (!HasExplicitForbiddenUsage(forbidden, normalizedText, usedSkillIds, plannedSkillIds, allowedSkillIds))
                continue;

            blocking.Add($"draft appears to use future/forbidden skill from mustNotUse: {forbidden}");
        }

        var accepted = blocking.Count == 0;
        var issues = new JsonArray();
        foreach (var item in blocking) issues.Add(item?.DeepClone());
        foreach (var item in advisory) issues.Add(item?.DeepClone());

        return new JsonObject
        {
            ["isAccepted"] = accepted,
            ["score"] = accepted ? (advisory.Count == 0 ? 96 : 88) : Math.Max(25, 96 - blocking.Count * 24 - advisory.Count * 4),
            ["issues"] = issues,
            ["blockingIssues"] = blocking,
            ["advisoryIssues"] = advisory,
            ["plannedSkillIds"] = ToJsonArray(plannedSkillIds),
            ["draftSkillIds"] = ToJsonArray(draftSkillIds),
            ["plannedStep"] = plannedStep.DeepClone()
        };
    }

    private static int NormalizeBridgeStepIndex(int stepIndex, int planCount)
    {
        if (stepIndex >= 0 && stepIndex < planCount) return stepIndex;
        if (stepIndex > 0 && stepIndex <= planCount) return stepIndex - 1;
        return stepIndex;
    }


    private static HashSet<string> BuildAllowedSkillIds(CourseSkillBridgeContext bridge, JsonObject plannedStep, HashSet<string> plannedSkillIds)
    {
        var allowed = new HashSet<string>(plannedSkillIds, StringComparer.OrdinalIgnoreCase);

        foreach (var id in BuildSkillIdSet(bridge.AcquiredSkills.Concat(bridge.TargetSkills)))
            allowed.Add(id);
        foreach (var id in BuildSkillIdSet(ReadStringArray(plannedStep["assumedSkills"])))
            allowed.Add(id);

        // These skills are usually implementation glue for small code-test tasks, not
        // separate pedagogical goals. Counting them as blockers caused every otherwise
        // valid bridge draft to be rejected even when the runner and model critic passed.
        foreach (var id in new[]
                 {
                     "program-structure", "console-output", "console-input-line", "variables",
                     "strings", "string-literals", "arithmetic", "parse-int"
                 })
            allowed.Add(id);

        if (plannedSkillIds.Contains("input-validation"))
        {
            allowed.Add("conditions");
            allowed.Add("comparison");
            allowed.Add("parse-int");
        }

        if (plannedSkillIds.Contains("split-input"))
        {
            allowed.Add("arrays");
            allowed.Add("parse-int");
            allowed.Add("arithmetic");
        }

        if (plannedSkillIds.Contains("multi-line-input"))
        {
            allowed.Add("parse-int");
            allowed.Add("arithmetic");
        }

        return allowed;
    }

    private static bool IsHardFutureSkill(string id)
        => id is "loops" or "collections" or "classes" or "files" or "linq" or "methods";

    private static bool HasExplicitForbiddenUsage(
        string forbidden,
        string normalizedText,
        HashSet<string> usedSkillIds,
        HashSet<string> plannedSkillIds,
        HashSet<string> allowedSkillIds)
    {
        var normalizedForbidden = NormalizeForLooseContains(forbidden);

        // Abstract labels from the LLM plan are not APIs. For example,
        // "advanced-parsers" must not be interpreted as "any parse/int.Parse usage".
        // Only concrete forbidden APIs/tokens should veto a draft.
        if (IsAbstractForbiddenLabel(normalizedForbidden))
            return false;

        var forbiddenIds = BuildSkillIdSet(CourseSkillAnalyzer.DetectCanonicalSkillIds(forbidden));
        if (forbiddenIds.Count > 0)
        {
            foreach (var id in forbiddenIds)
            {
                if (!plannedSkillIds.Contains(id) && !allowedSkillIds.Contains(id) && usedSkillIds.Contains(id))
                    return true;
            }
        }

        foreach (var token in ExtractSignificantForbiddenTokens(forbidden))
        {
            if (IsAbstractForbiddenLabel(token))
                continue;
            if (normalizedText.Contains(token, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static bool IsAbstractForbiddenLabel(string value)
    {
        var v = value.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(v)) return true;
        return v is "advanced" or "parsers" or "advanced parsers" or "advanced-parsers"
               or "advanced io" or "advanced-io"
               or "advanced parser" or "advanced-parser"
               or "exceptions as main flow" or "exceptions-as-main-flow";
    }

    private static HashSet<string> BuildSkillIdSet(IEnumerable<string> values)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
        {
            var detectedValues = CourseSkillAnalyzer.DetectCanonicalSkillIds(value).ToList();
            if (detectedValues.Count > 0)
            {
                foreach (var detected in detectedValues)
                    if (!string.IsNullOrWhiteSpace(detected)) result.Add(detected);
                continue;
            }

            var direct = CourseSkillAnalyzer.NormalizeSkillId(value);
            if (!string.IsNullOrWhiteSpace(direct)) result.Add(direct);
        }
        return result;
    }

    private static JsonArray ToJsonArray(IEnumerable<string> values)
    {
        var arr = new JsonArray();
        foreach (var value in values.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase)) arr.Add(value);
        return arr;
    }

    private static IEnumerable<string> ExtractSignificantForbiddenTokens(string value)
    {
        var normalized = NormalizeForLooseContains(value);
        foreach (var token in Regex.Split(normalized, @"[^a-zа-я0-9_.]+", RegexOptions.IgnoreCase))
        {
            var clean = token.Trim();
            if (clean.Length >= 6) yield return clean;
        }
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

        return text.Contains("mustnotuse")
               || text.Contains("must not use")
               || text.Contains("использует будущ")
               || text.Contains("future skill")
               || text.Contains("forbidden skill")
               || text.Contains("запрещ") && text.Contains("навык");
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
