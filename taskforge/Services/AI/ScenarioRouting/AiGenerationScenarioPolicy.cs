namespace taskforge.Services.AI;

internal static class AiGenerationScenarioPolicy
{
    private static readonly string[] GenerationVerbMarkers =
    {
        "сделай", "создай", "сгенер", "подготов", "собери", "нужна", "нужно", "хочу", "напиши", "наброс", "накидай", "придумай"
    };

    private static readonly string[] GenerationTargetMarkers =
    {
        "задач", "задачк", "сери", "лесенк", "пакет", "мини-программ", "маленьких программ", "программ", "черновик", "черновики", "наброс", "вариант", "тест", "quiz", "упражнен"
    };

    private static readonly string[] DirectResultMarkers =
    {
        "покажи итог", "покажи только итог", "итоговый набор", "без промежуточных фраз", "без промежуточных сообщений",
        "без промежуточных согласований", "без согласования", "не проси одобрение", "не проси у меня одобрение",
        "не спрашивай", "сразу", "сразу финал", "готовый результат"
    };

    public static bool LooksLikeScenarioGenerationIntent(string? text)
    {
        var low = (text ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(low))
            return false;

        var hasVerb = GenerationVerbMarkers.Any(low.Contains);
        var hasTarget = GenerationTargetMarkers.Any(low.Contains);
        if (!hasVerb || !hasTarget)
            return false;

        if (low.Contains("покажи существующие") || low.Contains("перечисли существующие") || low.Contains("список задан") || low.Contains("какие уже есть"))
            return false;

        return true;
    }

    public static bool RequestsDirectResultWithoutProgress(string? text)
    {
        var low = (text ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(low))
            return false;
        return DirectResultMarkers.Any(low.Contains);
    }

    public static bool SupportsDirectGeneration(AiGenerationScenarioProfile? profile)
    {
        if (profile == null)
            return true;

        return !string.Equals(profile.Family, "audit", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(profile.Family, "reordering", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(profile.Id, "practice-to-theory", StringComparison.OrdinalIgnoreCase);
    }

    public static bool ShouldBypassBlueprint(AiGenerationScenarioProfile? profile, bool preferAutonomy, string? latestGoal)
    {
        if (!SupportsDirectGeneration(profile))
            return false;
        if (preferAutonomy)
            return true;
        return RequestsDirectResultWithoutProgress(latestGoal);
    }
}
