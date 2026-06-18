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
    private static JsonObject BuildModelCriticDraftPayload(DraftSpec draft)
    {
        var obj = draft.ToArtifactData().DeepClone() as JsonObject ?? new JsonObject();
        if (obj["extra"] is not JsonObject extra)
            return obj;

        var compact = new JsonObject();
        foreach (var key in new[]
                 {
                     "bridgeSkillId", "bridgeStepIndex", "plannedBridgeSkillId",
                     "assumedSkills", "introducedSkills", "introducedSkillIds",
                     "targetSkills", "missingBridgeSkills", "skillBridgeReason",
                     "insertBeforeAssignmentId", "previousAssignmentTitle", "anchorAssignmentTitle"
                 })
        {
            if (extra[key] is not null)
                compact[key] = extra[key]!.DeepClone();
        }

        obj["extra"] = compact;
        return obj;
    }

    internal static JsonObject EvaluateBridgeConsistency(WorkflowState state)
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
                .Concat(ReadStringArray(draft.Extra["modelIntroducedSkillIds"]))
                .Concat(ReadStringArray(draft.Extra["modelBridgeSkillId"]))
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
        else if (inferredExtraSkillIds.Count > 0)
        {
            var blockingExtra = inferredExtraSkillIds
                .Where(id => !IsImplementationSupportSkill(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (blockingExtra.Count > 0)
                blocking.Add($"learning-bridge draft appears to use skills beyond the planned/current step: {string.Join(", ", blockingExtra)}");
            var supportExtra = inferredExtraSkillIds
                .Where(IsImplementationSupportSkill)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (supportExtra.Count > 0)
                advisory.Add($"draft uses implementation/support skills in addition to the planned step: {string.Join(", ", supportExtra)}");
        }

        if (plannedSkillIds.Count > 0 && declaredSkillIds.Count > 0 && !declaredSkillIds.Overlaps(plannedSkillIds) && !declaredSkillIds.All(allowedSkillIds.Contains))
            blocking.Add($"declared draft skill ids [{string.Join(", ", declaredSkillIds)}] do not match planned skill ids [{string.Join(", ", plannedSkillIds)}]");
        else if (plannedSkillIds.Count > 0 && draftSkillIds.Count > 0 && !draftSkillIds.Overlaps(plannedSkillIds))
            advisory.Add($"inferred draft skill ids [{string.Join(", ", draftSkillIds)}] do not directly mention planned skill ids [{string.Join(", ", plannedSkillIds)}]");

        // Hard mustNotUse checks are based on executable evidence only.
        // The student-facing description may mention a forbidden API as a warning or hint;
        // that must not be counted as actual usage.
        var executableEvidence = draft.ReferenceSolution ?? string.Empty;
        var usedSkillIds = BuildSkillIdSet(CourseSkillAnalyzer.DetectCanonicalSkillIds(executableEvidence));
        var normalizedText = NormalizeForLooseContains(executableEvidence);
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
            ["score"] = accepted ? (advisory.Count == 0 ? 96 : 88) : System.Math.Max(25, 96 - blocking.Count * 24 - advisory.Count * 4),
            ["issues"] = issues,
            ["blockingIssues"] = blocking,
            ["advisoryIssues"] = advisory,
            ["plannedSkillIds"] = ToJsonArray(plannedSkillIds),
            ["draftSkillIds"] = ToJsonArray(draftSkillIds),
            ["plannedStep"] = plannedStep.DeepClone()
        };
    }

}
