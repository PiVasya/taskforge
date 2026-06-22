namespace TaskForge.Support.Api.Domain;

public sealed class SupportMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TicketId { get; set; }
    public Guid? UserId { get; set; }
    public string AuthorRole { get; set; } = "user";
    public string Text { get; set; } = string.Empty;
    public string? Source { get; set; }
    public long? TelegramChatId { get; set; }
    public int? TelegramMessageId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
