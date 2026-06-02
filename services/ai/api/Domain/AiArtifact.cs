namespace TaskForge.Ai.Api.Domain;

public sealed class AiArtifact
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RunId { get; set; }
    public Guid ConversationId { get; set; }
    public string Type { get; set; } = "artifact";
    public string Title { get; set; } = "AI artifact";
    public string DataJson { get; set; } = "{}";
    public bool Applied { get; set; }
    public DateTimeOffset? AppliedAtUtc { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
