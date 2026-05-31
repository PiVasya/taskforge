namespace TaskForge.Support.Api.Domain;

public sealed class SupportTicket
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? UserId { get; set; }
    public string Subject { get; set; } = "Без темы";
    public string Status { get; set; } = "open";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
