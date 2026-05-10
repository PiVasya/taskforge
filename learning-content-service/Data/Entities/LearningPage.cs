namespace LearningContentService.Data.Entities;

public sealed class LearningPage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CourseId { get; set; }

    public string Slug { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;

    // summary, theory, note, dictionary, ct-years, practice-intro etc.
    public string Kind { get; set; } = "theory";

    // Можно хранить markdown для простого конспекта.
    public string? BodyMarkdown { get; set; }

    // Можно хранить structured blocks: heading, text, rule-card, example, task-list etc.
    public string? BodyJson { get; set; }

    public int SortOrder { get; set; }
    public bool IsPublished { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
