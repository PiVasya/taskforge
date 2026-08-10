namespace TaskForge.Education.Api.Domain;

public sealed class CourseMap
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RootCourseId { get; set; }
    public string DocumentJson { get; set; } = "{}";
    public int Version { get; set; }
    public Guid? UpdatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
