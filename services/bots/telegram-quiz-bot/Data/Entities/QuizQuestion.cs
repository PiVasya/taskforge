namespace TelegramQuizBot.Data.Entities;

public sealed class QuizQuestion
{
    public long Id { get; set; }
    public string Question { get; set; } = string.Empty;
    public string? Options { get; set; }
    public int? CorrectOptionId { get; set; }
    public string? Explanation { get; set; }
    public string Category { get; set; } = "Остальное";
    public string Subcategory { get; set; } = "Без подкатегории";
    public string Type { get; set; } = "quiz";
    public string Answer { get; set; } = string.Empty;
    public string Image { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }

    public string[] GetOptions() => string.IsNullOrWhiteSpace(Options)
        ? Array.Empty<string>()
        : Options.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
}
