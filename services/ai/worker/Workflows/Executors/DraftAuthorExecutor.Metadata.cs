using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Options;
using TaskForge.AiAgent.Contracts;
using TaskForge.AiAgent.Llm;
using TaskForge.AiAgent.Options;
using TaskForge.AiAgent.Prompts;
using TaskForge.AiAgent.Runtime;

namespace TaskForge.AiAgent.Workflows.Executors;

public sealed partial class DraftAuthorExecutor
{
    private static void ApplyBatchMetadata(List<DraftSpec> drafts, ClaimedAgentJob job, CourseSkillBridgeContext bridge)
    {
        for (var i = 0; i < drafts.Count; i++)
        {
            var stepIndex = bridge.BridgePlan is { Count: > 0 } ? i : drafts[i].SourceTaskIndex ?? i;
            ApplyDraftMetadata(drafts[i], job, bridge, stepIndex);
        }
    }

    private static void ApplyDraftMetadata(DraftSpec draft, ClaimedAgentJob job, CourseSkillBridgeContext bridge, int stepIndex)
    {
        draft.CourseId = job.CourseId;
        draft.BeforeAssignmentId ??= bridge.BeforeAssignmentId;
        if (bridge.BridgePlan is { Count: > 0 })
            draft.SourceTaskIndex = stepIndex;
        else
            draft.SourceTaskIndex ??= stepIndex;

        // Public tags stay clean. Learning-bridge and skill details are kept in Extra metadata, not in course cards.
        var requiredTags = new List<string> { "AI", "черновик" };
        draft.Tags = BuildPublicTags(MergeTags(draft.Tags, requiredTags));
        draft.Language = NormalizeLanguage(draft.Language);
        draft.Title = SanitizeStudentFacingTitle(draft.Title);
        draft.Difficulty = System.Math.Clamp(draft.Difficulty, 1, 3);
        draft.Rating = System.Math.Max(1, draft.Rating);
        AddBridgeExtra(draft, bridge, stepIndex);
        draft.Description = SanitizeStudentFacingDescription(draft.Description);

        // Do not replace model output with subject/language-specific canned code.
        // The author LLM must generate the actual solution from COURSE_SKILL_MAP;
        // this normalizer only keeps the visible text in tutorial form.
        draft.Description = EnsureLearningBridgeTutorialStyle(draft, bridge, stepIndex);
    }

    private static void AddBridgeExtra(DraftSpec draft, CourseSkillBridgeContext bridge, int stepIndex)
    {
        var plannedStep = bridge.BridgePlan != null && stepIndex >= 0 && stepIndex < bridge.BridgePlan.Count
            ? bridge.BridgePlan[stepIndex]
            : null;

        draft.Extra["bridgeStepIndex"] = stepIndex;
        if (plannedStep is not null)
        {
            if (draft.Extra["introducedSkills"] is not null && draft.Extra["modelIntroducedSkills"] is null)
                draft.Extra["modelIntroducedSkills"] = draft.Extra["introducedSkills"]!.DeepClone();

            draft.Extra["courseSkillMapStep"] = plannedStep.DeepClone();
            draft.Extra["assumedSkills"] = plannedStep["assumedSkills"]?.DeepClone();
            draft.Extra["introducedSkills"] = plannedStep["introducedSkills"]?.DeepClone();
            draft.Extra["skillBridgeReason"] = plannedStep["reason"]?.DeepClone();

            if (draft.Extra["bridgeSkillId"] is not null && draft.Extra["modelBridgeSkillId"] is null)
                draft.Extra["modelBridgeSkillId"] = draft.Extra["bridgeSkillId"]!.DeepClone();
            if (draft.Extra["introducedSkillIds"] is not null && draft.Extra["modelIntroducedSkillIds"] is null)
                draft.Extra["modelIntroducedSkillIds"] = draft.Extra["introducedSkillIds"]!.DeepClone();

            var skillId = plannedStep["skillId"]?.ToString();
            if (string.IsNullOrWhiteSpace(skillId))
                skillId = CourseSkillAnalyzer.NormalizeSkillId(string.Join(" ", ReadStringArray(plannedStep["introducedSkills"])));
            if (!string.IsNullOrWhiteSpace(skillId))
            {
                draft.Extra["plannedBridgeSkillId"] = skillId;
                draft.Extra["bridgeSkillId"] = skillId;
                draft.Extra["introducedSkillIds"] = ToJsonArray(new[] { skillId });
            }
        }
        draft.Extra["insertBeforeAssignmentId"] ??= bridge.BeforeAssignmentId?.ToString();
        draft.Extra["previousAssignmentTitle"] ??= bridge.PreviousTitle;
        draft.Extra["anchorAssignmentTitle"] ??= bridge.AnchorTitle;
        draft.Extra["requestedSkills"] ??= ToJsonArray(bridge.RequestedSkills);
        draft.Extra["acquiredSkillsBeforeAnchor"] ??= ToJsonArray(bridge.AcquiredSkills);
        draft.Extra["targetSkills"] ??= ToJsonArray(bridge.TargetSkills);
        draft.Extra["missingBridgeSkills"] ??= ToJsonArray(bridge.MissingBridgeSkills);
        draft.Extra["skillBridge"] ??= bridge.ToJsonObject();
    }

    private static JsonArray ToJsonArray(IEnumerable<string> values)
    {
        var arr = new JsonArray();
        foreach (var value in values.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
            arr.Add(value);
        return arr;
    }


    private static List<DraftSpec> AlignDraftsToBridgePlan(List<DraftSpec> drafts, CourseSkillBridgeContext bridge, int count)
    {
        var planCount = bridge.BridgePlan?.Count ?? 0;
        if (planCount == 0)
            return drafts.Take(count).ToList();

        var targetCount = System.Math.Min(count, planCount);
        var remaining = drafts.ToList();
        var aligned = new List<DraftSpec>();
        for (var stepIndex = 0; stepIndex < targetCount && remaining.Count > 0; stepIndex++)
        {
            var step = bridge.BridgePlan![stepIndex];
            var bestIndex = -1;
            var bestScore = int.MinValue;
            for (var i = 0; i < remaining.Count; i++)
            {
                var score = ScoreDraftForBridgeStep(remaining[i], step, stepIndex, planCount);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestIndex = i;
                }
            }

            if (bestIndex < 0)
                break;

            // If there is no semantic match, still pass the draft through the validator
            // under the planned step. The critic can then reject it or repair can target
            // the correct step. What we must not do is silently treat its own model skill
            // as the planned skill; AddBridgeExtra keeps modelBridgeSkillId for this reason.
            aligned.Add(remaining[bestIndex]);
            remaining.RemoveAt(bestIndex);
        }

        return aligned;
    }

    private static int ScoreDraftForBridgeStep(DraftSpec draft, JsonObject plannedStep, int stepIndex, int planCount)
    {
        var plannedIds = BuildSkillIdSet(
            ReadStringArray(plannedStep["skillId"])
                .Concat(ReadStringArray(plannedStep["introducedSkillIds"]))
                .Concat(ReadStringArray(plannedStep["introducedSkills"]))
                .Concat(ReadStringArray(plannedStep["titleHint"])));
        var draftIds = BuildSkillIdSet(
            ReadStringArray(draft.Extra["bridgeSkillId"])
                .Concat(ReadStringArray(draft.Extra["introducedSkillIds"]))
                .Concat(ReadStringArray(draft.Extra["introducedSkills"]))
                .Concat(new[] { draft.Title ?? string.Empty })
                .Concat(new[] { draft.Description ?? string.Empty }));

        var score = 0;
        if (plannedIds.Count > 0 && draftIds.Overlaps(plannedIds)) score += 20;
        if (draft.SourceTaskIndex.HasValue)
        {
            var normalized = NormalizeBridgeStepIndex(draft.SourceTaskIndex.Value, planCount);
            if (normalized == stepIndex) score += 4;
        }
        if (draftIds.Count == 0) score += 1;
        return score;
    }

    private static HashSet<string> BuildSkillIdSet(IEnumerable<string> values)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
        {
            var detected = CourseSkillAnalyzer.DetectCanonicalSkillIds(value).ToList();
            if (detected.Count > 0)
            {
                foreach (var id in detected)
                    AddCanonicalSkillId(result, id);
                continue;
            }

            var direct = CourseSkillAnalyzer.NormalizeSkillId(value);
            AddCanonicalSkillId(result, direct);
        }
        return result;
    }

    private static void AddCanonicalSkillId(HashSet<string> result, string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        var normalized = id.Trim().ToLowerInvariant();
        switch (normalized)
        {
            case "console-input":
            case "console input":
            case "console-readline":
            case "console-readline-echo":
                result.Add("console-input-line");
                return;
            case "parse-int-from-readline":
                result.Add("console-input-line");
                result.Add("parse-int");
                return;
            case "sum-two-ints-from-input":
                result.Add("console-input-line");
                result.Add("parse-int");
                result.Add("multi-line-input");
                result.Add("arithmetic");
                return;
            case "strings":
                result.Add("string-literals");
                return;
            default:
                result.Add(normalized);
                return;
        }
    }

    private static int NormalizeBridgeStepIndex(int stepIndex, int planCount)
    {
        if (stepIndex >= 0 && stepIndex < planCount) return stepIndex;
        if (stepIndex > 0 && stepIndex <= planCount) return stepIndex - 1;
        return stepIndex;
    }

    private static List<DraftSpec> NormalizeDraftOrder(List<DraftSpec> drafts)
    {
        var sourceIndexes = drafts.Where(x => x.SourceTaskIndex.HasValue).Select(x => x.SourceTaskIndex!.Value).ToList();
        var looksOneBased = sourceIndexes.Count > 0 && sourceIndexes.Min() == 1 && !sourceIndexes.Contains(0);
        return drafts
            .Select((draft, index) => new { draft, index, sort = draft.SourceTaskIndex.HasValue ? (looksOneBased ? draft.SourceTaskIndex.Value - 1 : draft.SourceTaskIndex.Value) : index })
            .OrderBy(x => x.sort)
            .ThenBy(x => x.index)
            .Select(x => x.draft)
            .ToList();
    }

}
