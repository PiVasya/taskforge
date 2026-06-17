namespace TaskForge.Ai.Api.Domain;

public sealed class AiConversation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "Новый диалог";
    public Guid? UserId { get; set; }
    public Guid? CourseId { get; set; }
    public Guid? AssignmentId { get; set; }
    public Guid? SupportTicketId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
