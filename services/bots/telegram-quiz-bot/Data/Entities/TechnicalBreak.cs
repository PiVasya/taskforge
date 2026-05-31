namespace TelegramQuizBot.Data.Entities;

public sealed class TechnicalBreak
{
    public int Id { get; set; } = 1;
    public bool IsActive { get; set; }
    public DateTimeOffset? EndTime { get; set; }
}
