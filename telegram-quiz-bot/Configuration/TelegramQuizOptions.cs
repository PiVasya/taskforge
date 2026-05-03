namespace TelegramQuizBot.Configuration;

public sealed class TelegramQuizOptions
{
    public string? TeacherBotToken { get; set; }
    public string? StudentBotToken { get; set; }
    public string? TeacherPassword { get; set; }
    public long AdminUserId { get; set; }
    public string ImagePrefix { get; set; } = "telegram-quiz/images";
    public bool ApplyMigrationsOnStartup { get; set; } = true;
    public int TelegramRequestTimeoutSeconds { get; set; } = 600;
}

