namespace TaskForge.Tasks.Api.Domain;

public sealed class Assignment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CourseId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Type { get; set; } = "code-test";
    public string Language { get; set; } = "csharp";
    public string? AllowedLanguagesCsv { get; set; }
    public string? Tags { get; set; }
    public int Difficulty { get; set; } = 1;
    public int Rating { get; set; } = 1;
    public string? StarterCode { get; set; }
    public string? TestsJson { get; set; }
    public string? CodeForbiddenCallsJson { get; set; }
    public string? CodeRequiredCallsJson { get; set; }
    public string? AnalyticsSettingsJson { get; set; }
    public bool IsVisible { get; set; } = true;
    public int Sort { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
