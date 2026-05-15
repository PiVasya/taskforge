using System.Text.Json;
using QuizTaskService.Data.Entities;

namespace QuizTaskService.DTO;

public sealed record QuizTaskDto(
    Guid Id,
    string Slug,
    string Type,
    string Title,
    string Prompt,
    string SubjectCode,
    string ExamCode,
    string? SectionCode,
    int Difficulty,
    string? TagsJson,
    string? SourceName,
    int? SourceYear,
    int CurrentVersion,
    bool IsPublished
)
{
    public static QuizTaskDto FromEntity(QuizTask x) => new(
        x.Id,
        x.Slug,
        x.Type,
        x.Title,
        x.Prompt,
        x.SubjectCode,
        x.ExamCode,
        x.SectionCode,
        x.Difficulty,
        x.TagsJson,
        x.SourceName,
        x.SourceYear,
        x.CurrentVersion,
        x.IsPublished);
}

public sealed record QuizTaskDetailsDto(
    QuizTaskDto Task,
    Guid VersionId,
    int VersionNumber,
    string DataJson,
    string ExplanationJson,
    bool Solved,
    decimal BestScorePercent
);

public sealed record AdminQuizTaskDetailsDto(
    QuizTaskDto Task,
    Guid VersionId,
    int VersionNumber,
    string DataJson,
    string CorrectAnswerJson,
    string ExplanationJson
);

public sealed record CreateQuizTaskRequest(
    string Slug,
    string Type,
    string Title,
    string Prompt,
    string SubjectCode,
    string ExamCode,
    string? SectionCode,
    int Difficulty,
    JsonElement? Tags,
    string? TagsJson,
    string? SourceName,
    int? SourceYear,
    JsonElement? Data,
    string? DataJson,
    JsonElement? CorrectAnswer,
    string? CorrectAnswerJson,
    JsonElement? Explanation,
    string? ExplanationJson,
    bool IsPublished = true
);

public sealed record SubmitQuizAttemptRequest(
    Guid? ClientAttemptId,
    JsonElement Answer,
    int? TimeSpentSeconds
);

public sealed record QuizAttemptResultDto(
    Guid AttemptId,
    Guid TaskId,
    Guid TaskVersionId,
    bool IsCorrect,
    decimal Score,
    decimal MaxScore,
    decimal ScorePercent,
    string ExplanationJson,
    QuizProgressDto Progress
);

public sealed record QuizSolutionDto(
    QuizTaskDto Task,
    Guid AttemptId,
    Guid TaskVersionId,
    string AnswerJson,
    bool IsCorrect,
    decimal Score,
    decimal MaxScore,
    decimal ScorePercent,
    string ExplanationJson,
    DateTime CreatedAt,
    QuizProgressDto? Progress
);

public sealed record QuizProgressDto(
    Guid TaskId,
    bool Solved,
    decimal BestScore,
    decimal BestScorePercent,
    int AttemptsCount,
    DateTime? FirstSolvedAt,
    DateTime LastAttemptAt
)
{
    public static QuizProgressDto FromEntity(QuizProgress x) => new(
        x.TaskId,
        x.Solved,
        x.BestScore,
        x.BestScorePercent,
        x.AttemptsCount,
        x.FirstSolvedAt,
        x.LastAttemptAt);
}
