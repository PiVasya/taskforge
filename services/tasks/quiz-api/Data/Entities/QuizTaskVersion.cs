namespace QuizTaskService.Data.Entities;

public sealed class QuizTaskVersion
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TaskId { get; set; }
    public int VersionNumber { get; set; } = 1;

    // JSON with options, UI hints, word metadata, etc.
    public string DataJson { get; set; } = "{}";

    // JSON with correct answer, for example: { "selected": ["о"] }
    public string CorrectAnswerJson { get; set; } = "{}";

    // JSON with explanation blocks or markdown text.
    public string ExplanationJson { get; set; } = "{}";

    public string? ChangeComment { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
