namespace TelegramQuizBot.Data.Entities;

public sealed class StudentContact
{
    public long UserId { get; set; }
    public long ChatId { get; set; }
    public string? Username { get; set; }
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? FullName { get; set; }
    public string? LanguageCode { get; set; }
    public DateTimeOffset FirstSeenAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastSeenAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastMessageAt { get; set; } = DateTimeOffset.UtcNow;
    public string LastMessageType { get; set; } = "unknown";
    public string? LastMessageText { get; set; }
    public int MessageCount { get; set; }
    public bool IsHidden { get; set; }
    public DateTimeOffset? HiddenAt { get; set; }
    public long? HiddenByTeacherId { get; set; }
    public string? Note { get; set; }
    public string SearchText { get; set; } = string.Empty;
}
