namespace TelegramQuizBot.Data.Entities;

public sealed class DiagnosticTest
{
    public long Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string QuestionIds { get; set; } = string.Empty;
}
