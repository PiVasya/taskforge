namespace TaskForge.Education.Api.Domain;

// The receipt and CourseMaps version are committed in the same transaction.
public sealed class CourseMapImportReceipt
{
    public Guid Id { get; set; }
    public Guid RootCourseId { get; set; }
    public Guid UserId { get; set; }
    public string RequestHash { get; set; } = "";
    public string ResponseJson { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
