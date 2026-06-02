namespace TaskForge.Ai.Api.Domain;

public sealed class AiRun
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ConversationId { get; set; }
    public string Status { get; set; } = "queued";
    public string JobType { get; set; } = "assistant_chat_turn";
    public string PayloadJson { get; set; } = "{}";
    public string? WorkerId { get; set; }
    public string? ErrorJson { get; set; }
    public DateTimeOffset? StartedAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
