namespace TelegramQuizBot.Data.Entities;

public sealed class ProgressEntry
{
    public long UserId { get; set; }
    public int Total { get; set; }
    public int Correct { get; set; }
    public int Incorrect { get; set; }
    public int Experience { get; set; }
}
