namespace TelegramQuizBot.Data.Entities;

public sealed class Category
{
    public string Name { get; set; } = string.Empty;
    public int OrderIndex { get; set; }
}
