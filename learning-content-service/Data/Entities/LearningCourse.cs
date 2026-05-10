namespace LearningContentService.Data.Entities;

public sealed class LearningCourse
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? ParentCourseId { get; set; }

    public string Slug { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? ShortTitle { get; set; }
    public string? Summary { get; set; }
    public string? Description { get; set; }

    // subject / exam / section make filtering easy: russian, ct-ce-2026, A1 etc.
    public string? SubjectCode { get; set; }
    public string? ExamCode { get; set; }
    public string? SectionCode { get; set; }

    public int SortOrder { get; set; }
    public bool IsPublished { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
