using System.Text.RegularExpressions;

namespace taskforge.Services.AI;

internal sealed record AiGenerationScenarioDefinition(
    string Id,
    string DisplayName,
    string Family,
    string DefaultBatchMode,
    bool PreferGuidedWalkthroughs,
    bool PreferTinySteps,
    bool ForceSmallPrograms,
    bool RequireExplicitIf,
    bool PreferSingleDeepTask,
    bool PreferCourseAudit,
    int DefaultCount,
    IReadOnlyList<string> Aliases,
    IReadOnlyList<string> AntiSignals)
{
    public int Score(string haystack, int requestedCount)
    {
        if (string.IsNullOrWhiteSpace(haystack))
            return 0;

        var score = 0;
        foreach (var alias in Aliases)
        {
            if (string.IsNullOrWhiteSpace(alias))
                continue;
            if (haystack.Contains(alias, StringComparison.OrdinalIgnoreCase))
                score += alias.Length >= 10 ? 4 : 3;
        }

        foreach (var anti in AntiSignals)
        {
            if (string.IsNullOrWhiteSpace(anti))
                continue;
            if (haystack.Contains(anti, StringComparison.OrdinalIgnoreCase))
                score -= anti.Length >= 10 ? 4 : 3;
        }

        if (PreferSingleDeepTask && requestedCount <= 1)
            score += 2;
        if ((Id is "step-by-step-ladder" or "micro-program-series") && requestedCount > 1)
            score += 2;
        if (RequireExplicitIf && Regex.IsMatch(haystack, @"\bif\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            score += 2;
        return score;
    }
}
