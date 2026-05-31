using System.Collections.Concurrent;
using TelegramQuizBot.Data.Entities;

namespace TelegramQuizBot.Bot;

public sealed class StudentBotStateStore
{
    public ConcurrentDictionary<string, ActivePollQuiz> ActivePolls { get; } = new();
    public ConcurrentDictionary<long, QuizQuestion> PendingTextAnswers { get; } = new();
}

public sealed class ActivePollQuiz
{
    public long UserId { get; set; }
    public long ChatId { get; set; }
    public QuizQuestion Quiz { get; set; } = new();
}
