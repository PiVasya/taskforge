using taskforge.Data.Models.DTO.AI;
using System.Text.RegularExpressions;

namespace taskforge.Services.AI;

internal static class AiGenerationScenarioRouter
{
    public static AiGenerationScenarioProfile Resolve(AiFoundryChatMemoryDto memory, string? prompt, string? sourceText, int requestedCount)
    {
        var haystack = BuildHaystack(memory, prompt, sourceText);
        var best = AiGenerationScenarioCatalog.Definitions
            .Select(def => new { Definition = def, Score = def.Score(haystack, requestedCount) })
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Definition.DefaultCount)
            .FirstOrDefault();

        var selected = best?.Definition ?? AiGenerationScenarioCatalog.Definitions.Last();
        if (best == null || best.Score <= 0)
            selected = AiGenerationScenarioCatalog.Definitions.Last();

        var matchedSignals = selected.Aliases
            .Where(alias => !string.IsNullOrWhiteSpace(alias) && haystack.Contains(alias, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(6)
            .ToList();

        var requireExplicitIf = selected.RequireExplicitIf;

        return new AiGenerationScenarioProfile
        {
            Id = selected.Id,
            DisplayName = selected.DisplayName,
            Family = selected.Family,
            DefaultBatchMode = requestedCount <= 1 ? "single-draft" : selected.DefaultBatchMode,
            PreferGuidedWalkthroughs = selected.PreferGuidedWalkthroughs,
            PreferTinySteps = selected.PreferTinySteps,
            ForceSmallPrograms = selected.ForceSmallPrograms,
            RequireExplicitIf = requireExplicitIf,
            PreferSingleDeepTask = selected.PreferSingleDeepTask,
            PreferCourseAudit = selected.PreferCourseAudit,
            DefaultCount = selected.DefaultCount,
            Score = Math.Max(best?.Score ?? 0, 0),
            MatchedSignals = matchedSignals,
        };
    }

    private static string BuildHaystack(AiFoundryChatMemoryDto memory, string? prompt, string? sourceText)
    {
        return string.Join(" ", new[]
        {
            memory.LatestExplicitInstruction,
            memory.LatestTeachingScript,
            memory.LatestIntentKind,
            memory.AgentState?.UserIntentSummary,
            memory.AgentState?.ObjectiveSummary,
            memory.AgentState?.PedagogyMode,
            prompt,
            sourceText,
            string.Join(" ", memory.RecentGoals ?? new List<string>()),
        }.Where(x => !string.IsNullOrWhiteSpace(x))).ToLowerInvariant();
    }
}
