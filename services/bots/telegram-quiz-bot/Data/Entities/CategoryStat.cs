namespace TelegramQuizBot.Data.Entities;

public sealed class CategoryStat
{
    public long UserId { get; set; }
    public string Category { get; set; } = string.Empty;
    public string Subcategory { get; set; } = string.Empty;
    public int Correct { get; set; }
    public int Incorrect { get; set; }
}
