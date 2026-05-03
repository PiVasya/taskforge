namespace TelegramQuizBot.Data.Entities;

public sealed class AuthorizedTeacher
{
    public long UserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
