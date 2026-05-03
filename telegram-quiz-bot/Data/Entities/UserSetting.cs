namespace TelegramQuizBot.Data.Entities;

public sealed class UserSetting
{
    public long UserId { get; set; }
    public string LearningMode { get; set; } = "normal";
    public string SelectedCategory { get; set; } = "all";
    public bool DiagnosticCompleted { get; set; }
}
