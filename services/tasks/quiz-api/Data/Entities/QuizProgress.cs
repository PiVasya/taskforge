namespace QuizTaskService.Data.Entities;

public sealed class QuizProgress
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TaskId { get; set; }
    public Guid UserId { get; set; }

    public bool Solved { get; set; }
    public decimal BestScore { get; set; }
    public decimal BestScorePercent { get; set; }
    public int AttemptsCount { get; set; }

    public Guid? LastAttemptId { get; set; }
    public DateTime? FirstSolvedAt { get; set; }
    public DateTime LastAttemptAt { get; set; } = DateTime.UtcNow;
}
