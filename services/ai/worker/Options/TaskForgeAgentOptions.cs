using System.ComponentModel.DataAnnotations;

namespace TaskForge.AiAgent.Options;

public sealed class TaskForgeAgentOptions
{
    [Required]
    public string Provider { get; set; } = "OpenRouter";

    [Required]
    public string OpenAiCompatibleBaseUrl { get; set; } = "https://openrouter.ai/api/v1";

    public string ApiKey { get; set; } = string.Empty;

    [Required]
    public string Model { get; set; } = "openai/gpt-4o-mini";

    [Required]
    public string AgentName { get; set; } = "TaskForgeCoordinator";

    [Range(30, 3600)]
    public int MaxJobSeconds { get; set; } = 420;

    [Range(0, 5)]
    public int MaxDraftRepairAttempts { get; set; } = 2;

    [Range(1, 50)]
    public int MaxDraftsPerRun { get; set; } = 50;

    [Range(4000, 200000)]
    public int MaxContextCharacters { get; set; } = 36000;

    public bool IncludeRawModelTextInDebug { get; set; }

    public bool EnableDangerousWriteTools { get; set; }

    public string DefaultLanguage { get; set; } = "cpp";

    public bool EnableAdaptiveAgentLoop { get; set; } = true;

    [Range(3, 24)]
    public int MaxAgentLoopSteps { get; set; } = 12;

    [Range(1, 12)]
    public int MaxAgentActionsPerStep { get; set; } = 8;

    [Range(1, 500)]
    public int MaxPatchOperationsPerRun { get; set; } = 200;

    [Range(8000, 200000)]
    public int MaxAgentStateCharacters { get; set; } = 64000;

}

public sealed class TaskForgeInternalApiOptions
{
    [Required]
    public string BaseUrl { get; set; } = "http://ai-api:8080";

    public string ApiKey { get; set; } = string.Empty;

    [Range(100, 30000)]
    public int ClaimBatchDelayMs { get; set; } = 1200;

    [Range(5, 300)]
    public int HeartbeatSeconds { get; set; } = 30;

    [Range(15, 600)]
    public int RequestTimeoutSeconds { get; set; } = 120;
}
