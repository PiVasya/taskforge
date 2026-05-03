namespace TelegramQuizBot.Data.Entities;

public sealed class StartLogEntry
{
    public long Id { get; set; }
    public long UserId { get; set; }
    public string? Username { get; set; }
    public string? FullName { get; set; }
    public DateTimeOffset Timestamp { get; set; }
}
