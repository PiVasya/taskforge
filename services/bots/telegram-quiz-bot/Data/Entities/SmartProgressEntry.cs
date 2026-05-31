namespace TelegramQuizBot.Data.Entities;

public sealed class SmartProgressEntry
{
    public long UserId { get; set; }
    public string Subcategory { get; set; } = string.Empty;
    public int Correct { get; set; }
    public int Incorrect { get; set; }
}
