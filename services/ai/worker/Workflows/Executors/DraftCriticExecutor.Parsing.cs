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
