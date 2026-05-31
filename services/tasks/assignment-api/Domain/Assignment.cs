namespace TaskForge.Tasks.Api.Domain;

public sealed class Assignment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CourseId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Type { get; set; } = "code-test";
    public string Language { get; set; } = "csharp";
    public string? StarterCode { get; set; }
    public string? TestsJson { get; set; }
    public bool IsVisible { get; set; } = true;
    public int Sort { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
