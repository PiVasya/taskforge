namespace TelegramQuizBot.Data.Entities;

public sealed class UserAnswer
{
    public long Id { get; set; }
    public long UserId { get; set; }
    public string? Username { get; set; }
    public long QuizId { get; set; }
    public bool Correct { get; set; }
    public DateTimeOffset Timestamp { get; set; }
}
