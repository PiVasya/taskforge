using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using QuizTaskService.Data;
using QuizTaskService.Data.Entities;
using QuizTaskService.DTO;
using QuizTaskService.Services;
using static QuizTaskService.Services.Access.QuizTaskAccessService;
using static QuizTaskService.Services.Common.QuizTaskCommonService;
using static QuizTaskService.Services.Serialization.QuizTaskSerializationService;

namespace QuizTaskService.Services.Mapping;

internal static class QuizTaskMappingService
{
    internal static IResult? ValidateTaskRequest(CreateQuizTaskRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Slug) || string.IsNullOrWhiteSpace(req.Title) || string.IsNullOrWhiteSpace(req.Prompt))
        {
            return Results.BadRequest(new { message = "Slug, Title and Prompt are required" });
        }

        var sectionCode = NormalizeSectionCode(req.SectionCode);
        if (!string.IsNullOrWhiteSpace(sectionCode) && !System.Text.RegularExpressions.Regex.IsMatch(sectionCode, "^[AB][0-9]+$"))
        {
            return Results.BadRequest(new { message = "sectionCode must look like A1, A31, B1 or B11" });
        }

        var explanationJson = JsonOrDefault(req.Explanation, req.ExplanationJson, "{}");
        if (string.IsNullOrWhiteSpace(ExtractExplanationText(explanationJson)))
        {
            return Results.BadRequest(new { message = "Explanation is required" });
        }

        var type = NormalizeTaskType(req.Type, sectionCode);
        var dataJson = JsonOrDefault(req.Data, req.DataJson, "{}");
        var correctAnswerJson = JsonOrDefault(req.CorrectAnswer, req.CorrectAnswerJson, "{}");
        if (string.IsNullOrWhiteSpace(ExtractAnswerText(correctAnswerJson)))
        {
            return Results.BadRequest(new { message = "Correct answer is required" });
        }

        if (string.Equals(type, "single-choice", StringComparison.OrdinalIgnoreCase) || string.Equals(type, "multiple-choice", StringComparison.OrdinalIgnoreCase))
        {
            var choiceValidation = ValidateChoiceAnswer(dataJson, correctAnswerJson, allowMultiple: string.Equals(type, "multiple-choice", StringComparison.OrdinalIgnoreCase));
            if (choiceValidation != null) return choiceValidation;
        }

        return null;
    }

    internal static IResult? ValidateChoiceAnswer(string dataJson, string correctAnswerJson, bool allowMultiple)
    {
        var options = ExtractOptions(dataJson);
        if (options.Count == 0)
        {
            return Results.BadRequest(new { message = "Choice task must contain data.options" });
        }

        var selected = ExtractSelectedAnswers(correctAnswerJson);
        if (selected.Count == 0)
        {
            return Results.BadRequest(new { message = "Choice task must contain correctAnswer.selected" });
        }

        if (!allowMultiple && selected.Count != 1)
        {
            return Results.BadRequest(new { message = "Single-choice task must contain exactly one correct answer" });
        }

        var normalizedOptions = options.Select(NormalizeForCompare).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unknown = selected.Where(x => !normalizedOptions.Contains(NormalizeForCompare(x))).ToList();
        if (unknown.Count > 0)
        {
            return Results.BadRequest(new { message = "Correct answer must match one of data.options", unknownAnswers = unknown });
        }

        return null;
    }

    internal static async Task<Dictionary<Guid, QuizTaskVersion?>> LoadCurrentVersionsAsync(QuizDbContext db, IReadOnlyCollection<QuizTask> tasks)
    {
        var taskIds = tasks.Select(x => x.Id).ToList();
        if (taskIds.Count == 0) return new Dictionary<Guid, QuizTaskVersion?>();

        var versions = await db.TaskVersions.AsNoTracking()
            .Where(x => taskIds.Contains(x.TaskId))
            .ToListAsync();

        return tasks.ToDictionary(
            task => task.Id,
            task => versions.FirstOrDefault(version => version.TaskId == task.Id && version.VersionNumber == task.CurrentVersion));
    }

    internal static AdminQuizTaskDetailsDto ToAdminDto(QuizTask task, QuizTaskVersion? version)
    {
        return new AdminQuizTaskDetailsDto(
            QuizTaskDto.FromEntity(task),
            version?.Id ?? Guid.Empty,
            version?.VersionNumber ?? task.CurrentVersion,
            version?.DataJson ?? "{}",
            version?.CorrectAnswerJson ?? "{}",
            version?.ExplanationJson ?? "{}");
    }

    internal static QuizAttemptResultDto ToResult(QuizAttempt attempt, string explanationJson, QuizProgress progress)
    {
        var percent = attempt.MaxScore <= 0 ? 0 : Math.Round(attempt.Score / attempt.MaxScore * 100m, 2);
        return new QuizAttemptResultDto(
            attempt.Id,
            attempt.TaskId,
            attempt.TaskVersionId,
            attempt.IsCorrect,
            attempt.Score,
            attempt.MaxScore,
            percent,
            explanationJson,
            QuizProgressDto.FromEntity(progress));
    }

}
