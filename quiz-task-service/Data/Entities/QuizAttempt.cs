namespace QuizTaskService.Data.Entities;

public sealed class QuizAttempt
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TaskId { get; set; }
    public Guid TaskVersionId { get; set; }

    // UserId comes from TaskForge JWT. No FK to auth DB: service boundary.
    public Guid UserId { get; set; }

    // Idempotency key from frontend. Prevents double submit.
    public Guid ClientAttemptId { get; set; }

    public string AnswerJson { get; set; } = "{}";
    public bool IsCorrect { get; set; }
    public decimal Score { get; set; }
    public decimal MaxScore { get; set; } = 1;
    public int? TimeSpentSeconds { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
