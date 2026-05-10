using System.Text.Json;
using LearningContentService.Data.Entities;

namespace LearningContentService.DTO;

public sealed record LearningCourseDto(
    Guid Id,
    Guid? ParentCourseId,
    string Slug,
    string Title,
    string? ShortTitle,
    string? Summary,
    string? Description,
    string? SubjectCode,
    string? ExamCode,
    string? SectionCode,
    int SortOrder,
    bool IsPublished
)
{
    public static LearningCourseDto FromEntity(LearningCourse x) => new(
        x.Id,
        x.ParentCourseId,
        x.Slug,
        x.Title,
        x.ShortTitle,
        x.Summary,
        x.Description,
        x.SubjectCode,
        x.ExamCode,
        x.SectionCode,
        x.SortOrder,
        x.IsPublished);
}

public sealed class LearningCourseTreeDto
{
    public Guid Id { get; set; }
    public Guid? ParentCourseId { get; set; }
    public string Slug { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? ShortTitle { get; set; }
    public string? Summary { get; set; }
    public string? SubjectCode { get; set; }
    public string? ExamCode { get; set; }
    public string? SectionCode { get; set; }
    public int SortOrder { get; set; }
    public List<LearningCourseTreeDto> Children { get; set; } = new();
}

public sealed record LearningPageDto(
    Guid Id,
    Guid CourseId,
    string Slug,
    string Title,
    string Kind,
    string? BodyMarkdown,
    string? BodyJson,
    int SortOrder,
    bool IsPublished
)
{
    public static LearningPageDto FromEntity(LearningPage x) => new(
        x.Id,
        x.CourseId,
        x.Slug,
        x.Title,
        x.Kind,
        x.BodyMarkdown,
        x.BodyJson,
        x.SortOrder,
        x.IsPublished);
}

public sealed record LearningConspectDto(
    Guid Id,
    Guid CourseId,
    string Slug,
    string Title,
    string? Subtitle,
    string? Lead,
    string? SubjectCode,
    string? ExamCode,
    string? SectionCode,
    string Kind,
    int SortOrder,
    int EstimatedMinutes,
    string? BadgesJson,
    bool IsPublished,
    DateTime UpdatedAt
)
{
    public static LearningConspectDto FromEntity(LearningConspect x) => new(
        x.Id,
        x.CourseId,
        x.Slug,
        x.Title,
        x.Subtitle,
        x.Lead,
        x.SubjectCode,
        x.ExamCode,
        x.SectionCode,
        x.Kind,
        x.SortOrder,
        x.EstimatedMinutes,
        x.BadgesJson,
        x.IsPublished,
        x.UpdatedAt);
}

public sealed record LearningConspectTaskLinkDto(
    Guid Id,
    Guid ConspectId,
    Guid? TaskId,
    string TaskType,
    string SourceService,
    string? TaskSlug,
    string? TaskFilterJson,
    string Title,
    string ButtonText,
    string? GroupTitle,
    string? AnchorBlockId,
    bool IsRequired,
    int SortOrder
)
{
    public static LearningConspectTaskLinkDto FromEntity(LearningConspectTaskLink x) => new(
        x.Id,
        x.ConspectId,
        x.TaskId,
        x.TaskType,
        x.SourceService,
        x.TaskSlug,
        x.TaskFilterJson,
        x.Title,
        x.ButtonText,
        x.GroupTitle,
        x.AnchorBlockId,
        x.IsRequired,
        x.SortOrder);
}

public sealed record LearningConspectDetailsDto(
    LearningConspectDto Conspect,
    string ContentJson,
    string? SearchText,
    IReadOnlyList<LearningConspectTaskLinkDto> TaskLinks
);

public sealed record LearningTaskLinkDto(
    Guid Id,
    Guid CourseId,
    Guid TaskId,
    string TaskType,
    string SourceService,
    string? TaskSlug,
    string? Title,
    string? GroupTitle,
    bool IsRequired,
    int SortOrder
)
{
    public static LearningTaskLinkDto FromEntity(LearningCourseTaskLink x) => new(
        x.Id,
        x.CourseId,
        x.TaskId,
        x.TaskType,
        x.SourceService,
        x.TaskSlug,
        x.Title,
        x.GroupTitle,
        x.IsRequired,
        x.SortOrder);
}

public sealed record LearningCourseOutlineDto(
    LearningCourseDto Course,
    IReadOnlyList<LearningCourseDto> Children,
    IReadOnlyList<LearningPageDto> Pages,
    IReadOnlyList<LearningConspectDto> Conspects,
    IReadOnlyList<LearningTaskLinkDto> Tasks
);

public sealed record CreateLearningCourseRequest(
    Guid? ParentCourseId,
    string Slug,
    string Title,
    string? ShortTitle,
    string? Summary,
    string? Description,
    string? SubjectCode,
    string? ExamCode,
    string? SectionCode,
    int SortOrder,
    bool IsPublished = true
);

public sealed record UpdateLearningCourseRequest(
    Guid? ParentCourseId,
    string? Slug,
    string? Title,
    string? ShortTitle,
    string? Summary,
    string? Description,
    string? SubjectCode,
    string? ExamCode,
    string? SectionCode,
    int? SortOrder,
    bool? IsPublished
);

public sealed record CreateLearningPageRequest(
    string Slug,
    string Title,
    string Kind,
    string? BodyMarkdown,
    JsonElement? Body,
    string? BodyJson,
    int SortOrder,
    bool IsPublished = true
);

public sealed record CreateLearningConspectRequest(
    string Slug,
    string Title,
    string? Subtitle,
    string? Lead,
    string? SubjectCode,
    string? ExamCode,
    string? SectionCode,
    string? Kind,
    int SortOrder,
    int EstimatedMinutes,
    JsonElement? Badges,
    string? BadgesJson,
    JsonElement? Content,
    string? ContentJson,
    string? SearchText,
    bool IsPublished = true
);

public sealed record UpdateLearningConspectRequest(
    string? Slug,
    string? Title,
    string? Subtitle,
    string? Lead,
    string? SubjectCode,
    string? ExamCode,
    string? SectionCode,
    string? Kind,
    int? SortOrder,
    int? EstimatedMinutes,
    JsonElement? Badges,
    string? BadgesJson,
    JsonElement? Content,
    string? ContentJson,
    string? SearchText,
    bool? IsPublished
);

public sealed record CreateLearningTaskLinkRequest(
    Guid TaskId,
    string TaskType,
    string SourceService,
    string? TaskSlug,
    string? Title,
    string? GroupTitle,
    bool IsRequired,
    int SortOrder
);

public sealed record CreateLearningConspectTaskLinkRequest(
    Guid? TaskId,
    string? TaskType,
    string? SourceService,
    string? TaskSlug,
    JsonElement? TaskFilter,
    string? TaskFilterJson,
    string Title,
    string? ButtonText,
    string? GroupTitle,
    string? AnchorBlockId,
    bool IsRequired,
    int SortOrder
);
