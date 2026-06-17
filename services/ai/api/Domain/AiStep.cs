namespace TaskForge.Ai.Api.Domain;

public sealed class AiStep
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RunId { get; set; }
    public Guid ConversationId { get; set; }
    public int Seq { get; set; }
    public string Kind { get; set; } = "worker";
    public string Status { get; set; } = "running";
    public string ActionName { get; set; } = "agent_step";
    public string Title { get; set; } = "AI-шаг";
    public string? Summary { get; set; }
    public string? DataJson { get; set; }
    public bool IsVisibleToUser { get; set; } = true;
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
