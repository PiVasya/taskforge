namespace taskforge.Services.AI;

internal sealed class AiGenerationScenarioProfile
{
    public string Id { get; init; } = "general-topic-pack";
    public string DisplayName { get; init; } = "Общий пакет задач";
    public string Family { get; init; } = "general";
    public string DefaultBatchMode { get; init; } = "topic-pack";
    public bool PreferGuidedWalkthroughs { get; init; }
    public bool PreferTinySteps { get; init; }
    public bool ForceSmallPrograms { get; init; }
    public bool RequireExplicitIf { get; init; }
    public bool PreferSingleDeepTask { get; init; }
    public bool PreferCourseAudit { get; init; }
    public int DefaultCount { get; init; } = 3;
    public int Score { get; init; }
    public List<string> MatchedSignals { get; init; } = new();
    public string Summary => $"{DisplayName} ({Id})";
}
