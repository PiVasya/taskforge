namespace TaskForge.Tasks.Api.Domain;

public sealed class CodeAssignmentSpec
{
    public Guid AssignmentId { get; set; }
    public string Language { get; set; } = "csharp";
    public string? AllowedLanguagesCsv { get; set; }
    public string? StarterCode { get; set; }
    public string TestsJson { get; set; } = "[]";
    public string? CodeForbiddenCallsJson { get; set; }
    public string? CodeRequiredCallsJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
