using System.Collections.Concurrent;

namespace TelegramQuizBot.Bot;

public sealed class TeacherBotStateStore
{
    public ConcurrentDictionary<long, TeacherDraftQuestion> Drafts { get; } = new();
}

public sealed class TeacherDraftQuestion
{
    public string Type { get; set; } = "text";
    public string Step { get; set; } = "image";
    public string? ImageKey { get; set; }
    public string? Question { get; set; }
    public string? Answer { get; set; }
    public string? Explanation { get; set; }
    public string? Category { get; set; }
    public string? Subcategory { get; set; }
}
