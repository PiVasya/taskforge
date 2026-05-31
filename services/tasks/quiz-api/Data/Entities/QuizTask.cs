namespace QuizTaskService.Data.Entities;

public sealed class QuizTask
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Slug { get; set; } = string.Empty;
    public string Type { get; set; } = "vowel-choice";
    public string Title { get; set; } = string.Empty;
    public string Prompt { get; set; } = string.Empty;

    public string SubjectCode { get; set; } = "russian";
    public string ExamCode { get; set; } = "ct-ce-2026";
    public string? SectionCode { get; set; }

    public int Difficulty { get; set; } = 1;
    public string? TagsJson { get; set; }
    public string? SourceName { get; set; }
    public int? SourceYear { get; set; }

    public bool IsPublished { get; set; } = true;
    public int CurrentVersion { get; set; } = 1;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
