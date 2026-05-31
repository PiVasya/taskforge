namespace LearningContentService.Data.Entities;

public sealed class LearningCourseTaskLink
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CourseId { get; set; }

    // Id задачи в сервисе-владельце. Для quiz это QuizTask.Id.
    public Guid TaskId { get; set; }

    // quiz-mini, quiz-test, code, image etc.
    public string TaskType { get; set; } = "quiz-mini";

    // quiz-task-service, code-task-service, image-task-service etc.
    public string SourceService { get; set; } = "quiz-task-service";

    public string? TaskSlug { get; set; }
    public string? Title { get; set; }
    public string? GroupTitle { get; set; }

    public bool IsRequired { get; set; }
    public int SortOrder { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
