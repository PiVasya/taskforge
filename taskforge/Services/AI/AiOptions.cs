using System.ComponentModel.DataAnnotations;

namespace taskforge.Services.AI;

public sealed class AiOptions
{
    public bool Enabled { get; set; } = false;

    [MaxLength(256)]
    public string? SystemUserEmail { get; set; }

    [MaxLength(128)]
    public string SystemUserFirstName { get; set; } = "TaskForge";

    [MaxLength(128)]
    public string SystemUserLastName { get; set; } = "AI";

    [MaxLength(256)]
    public string DefaultModel { get; set; } = "qwen3:14b";

    public bool RequireHumanApproval { get; set; } = true;

    public bool RequirePassedSelfCheckForPublish { get; set; } = true;
}
