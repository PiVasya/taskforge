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
    private static int NormalizeBridgeStepIndex(int stepIndex, int planCount)
    {
        if (stepIndex >= 0 && stepIndex < planCount) return stepIndex;
        if (stepIndex > 0 && stepIndex <= planCount) return stepIndex - 1;
        return stepIndex;
    }


    private static HashSet<string> BuildAllowedSkillIds(CourseSkillBridgeContext bridge, JsonObject plannedStep, HashSet<string> plannedSkillIds)
    {
        var allowed = new HashSet<string>(plannedSkillIds, StringComparer.OrdinalIgnoreCase);

        foreach (var id in BuildSkillIdSet(bridge.AcquiredSkills))
            allowed.Add(id);
        foreach (var id in BuildSkillIdSet(ReadStringArray(plannedStep["assumedSkills"])))
            allowed.Add(id);

        var forbidden = BuildSkillIdSet(ReadStringArray(plannedStep["mustNotUse"]));

        // Only language-agnostic scaffolding is treated as support by default.
        // Topic skills such as parsing, splitting, arrays, conditions or loops must
        // come from the current planned step or from explicit assumed/acquired skills.
        foreach (var id in new[] { "program-structure", "console-output", "variables" })
        {
            if (!forbidden.Contains(id)) allowed.Add(id);
        }

        foreach (var id in BuildPlannedDependencySkillIds(plannedSkillIds))
        {
            if (!forbidden.Contains(id)) allowed.Add(id);
        }

        allowed.ExceptWith(forbidden);
        return allowed;
    }

    private static IEnumerable<string> BuildPlannedDependencySkillIds(HashSet<string> plannedSkillIds)
    {
        // These are dependency relationships between abstract skill ids, not canned
        // assignment templates. They only expand the current planned step; they never
        // create a new topic on their own.
        if (plannedSkillIds.Contains("read-single-value-from-console"))
        {
            // A bridge step named "read one value from console" is a composite
            // micro-skill: the visible task may use the concrete C# operations below
            // without introducing a future topic. This is intentionally generic at
            // the skill-taxonomy level, not заранее зашитый to a particular assignment.
            yield return "console-input-line";
            yield return "parse-int";
            yield return "variables";
        }

        if (plannedSkillIds.Contains("parse-int"))
        {
            yield return "console-input-line";
            yield return "variables";
        }

        if (plannedSkillIds.Contains("console-input-line"))
        {
            yield return "variables";
        }

        if (plannedSkillIds.Contains("input-validation"))
        {
            yield return "console-input-line";
            yield return "parse-int";
            yield return "conditions";
            yield return "comparison";
        }

        if (plannedSkillIds.Contains("split-input"))
        {
            yield return "console-input-line";
            yield return "arrays";
        }
    }

    private static bool IsImplementationSupportSkill(string id)
        => id is "program-structure" or "console-output" or "string-literals" or "variables";

    private static bool HasExplicitForbiddenUsage(
        string forbidden,
        string normalizedText,
        HashSet<string> usedSkillIds,
        HashSet<string> plannedSkillIds,
        HashSet<string> allowedSkillIds)
    {
        var normalizedForbidden = NormalizeForLooseContains(forbidden);

        // Abstract labels from the LLM plan are not APIs. For example,
        // "advanced-parsers" must not be interpreted as "any parsing API usage".
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
                    AddCanonicalSkillId(result, detected);
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

}
