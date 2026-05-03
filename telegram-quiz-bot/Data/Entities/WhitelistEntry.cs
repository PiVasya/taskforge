namespace TelegramQuizBot.Data.Entities;

public sealed class WhitelistEntry
{
    public long UserId { get; set; }
    public DateTimeOffset? ExpireTime { get; set; }
    public DateTimeOffset? LastNotification { get; set; }
}
