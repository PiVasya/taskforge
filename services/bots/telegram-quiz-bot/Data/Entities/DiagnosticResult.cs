namespace TelegramQuizBot.Data.Entities;

public sealed class DiagnosticResult
{
    public long UserId { get; set; }
    public string Subcategory { get; set; } = string.Empty;
    public bool Correct { get; set; }
}
