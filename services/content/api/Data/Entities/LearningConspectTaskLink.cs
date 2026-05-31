namespace LearningContentService.Data.Entities;

public sealed class LearningConspectTaskLink
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ConspectId { get; set; }

    // Если задача живет в quiz-task-service, сюда можно записать QuizTask.Id.
    // Поле nullable, потому что на этапе наполнения конспекта удобнее ссылаться по slug/filter.
    public Guid? TaskId { get; set; }

    // quiz-mini, quiz-test, code, image, task-filter etc.
    public string TaskType { get; set; } = "quiz-mini";

    // quiz-task-service, code-task-service, image-task-service etc.
    public string SourceService { get; set; } = "quiz-task-service";

    public string? TaskSlug { get; set; }
    public string? TaskFilterJson { get; set; }

    public string Title { get; set; } = string.Empty;
    public string ButtonText { get; set; } = "К заданиям";
    public string? GroupTitle { get; set; }
    public string? AnchorBlockId { get; set; }

    public bool IsRequired { get; set; }
    public int SortOrder { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
