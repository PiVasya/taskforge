namespace TaskForge.Tasks.Api.Domain;

public sealed class Assignment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CourseId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Type { get; set; } = "code-test";
    // Language remains a lightweight denormalized display/search value for executable assignments.
    // Authoritative executable configuration lives in CodeAssignmentSpec/ImageAssignmentSpec.
    public string Language { get; set; } = "csharp";

    // Legacy compatibility columns. Do not add new runtime behavior that reads these directly.
    // They stay for one rollout so the startup backfill can copy existing assignments into
    // type-specific spec tables before a later cleanup migration removes the old payload columns.
    public string? AllowedLanguagesCsv { get; set; }
    public string? Tags { get; set; }
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
