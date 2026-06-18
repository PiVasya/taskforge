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
using static QuizTaskService.Services.Mapping.QuizTaskMappingService;
using static QuizTaskService.Services.Serialization.QuizTaskSerializationService;

namespace QuizTaskService.Endpoints;

internal static partial class QuizTaskEndpoints
{
    private static WebApplication MapQuizEndpoints(WebApplication app)
    {
        app.MapGet("/api/quiz/tasks", async (QuizDbContext db, ClaimsPrincipal user, string? subjectCode, string? examCode, string? sectionCode, string? type, bool includeDraft = false) =>
        {
            var query = db.Tasks.AsNoTracking().AsQueryable();
            var canSeeDrafts = includeDraft && CanEditQuiz(user);
            if (!canSeeDrafts) query = query.Where(x => x.IsPublished);
            if (!string.IsNullOrWhiteSpace(subjectCode)) query = query.Where(x => x.SubjectCode == subjectCode);
            if (!string.IsNullOrWhiteSpace(examCode)) query = query.Where(x => x.ExamCode == examCode);
            if (!string.IsNullOrWhiteSpace(sectionCode))
            {
                var normalizedSectionCode = NormalizeSectionCode(sectionCode);
                query = query.Where(x => x.SectionCode == normalizedSectionCode);
            }
            if (!string.IsNullOrWhiteSpace(type)) query = query.Where(x => x.Type == type);

            var taskEntities = await query
                .OrderBy(x => x.SectionCode)
                .ThenBy(x => x.Difficulty)
                .ThenBy(x => x.Title)
                .ToListAsync();

            return Results.Ok(taskEntities.Select(QuizTaskDto.FromEntity).ToList());
        });

        app.MapGet("/api/quiz/tasks/{idOrSlug}", async (QuizDbContext db, ClaimsPrincipal user, string idOrSlug) =>
        {
            var isGuid = Guid.TryParse(idOrSlug, out var id);
            var canSeeDrafts = CanEditQuiz(user);
            var task = await db.Tasks.AsNoTracking().FirstOrDefaultAsync(x => isGuid ? x.Id == id : x.Slug == idOrSlug);
            if (task == null || (!task.IsPublished && !canSeeDrafts)) return Results.NotFound();

            var version = await db.TaskVersions.AsNoTracking()
                .Where(x => x.TaskId == task.Id && x.VersionNumber == task.CurrentVersion)
                .FirstOrDefaultAsync();

            if (version == null) return Results.NotFound(new { message = "Task version not found" });

            var userId = TryGetUserId(user);
            var progress = userId.HasValue
                ? await db.Progress.AsNoTracking().FirstOrDefaultAsync(x => x.UserId == userId.Value && x.TaskId == task.Id)
                : null;

            return Results.Ok(new QuizTaskDetailsDto(
                QuizTaskDto.FromEntity(task),
                version.Id,
                version.VersionNumber,
                version.DataJson,
                version.ExplanationJson,
                progress?.Solved ?? false,
                progress?.BestScorePercent ?? 0));
        });

        app.MapPost("/api/quiz/tasks/{taskId:guid}/attempts", [Authorize] async (QuizDbContext db, ClaimsPrincipal user, Guid taskId, [FromBody] SubmitQuizAttemptRequest req) =>
        {
            var userId = TryGetUserId(user);
            if (!userId.HasValue) return Results.Unauthorized();

            var clientAttemptId = req.ClientAttemptId ?? Guid.NewGuid();
            var existingByClientAttempt = await db.Attempts.AsNoTracking()
                .FirstOrDefaultAsync(x => x.UserId == userId.Value && x.ClientAttemptId == clientAttemptId);
            if (existingByClientAttempt != null)
            {
                var existingProgress = await db.Progress.AsNoTracking()
                    .FirstOrDefaultAsync(x => x.UserId == userId.Value && x.TaskId == existingByClientAttempt.TaskId);
                var existingVersion = await db.TaskVersions.AsNoTracking().FirstAsync(x => x.Id == existingByClientAttempt.TaskVersionId);

                existingProgress ??= new QuizProgress
                {
                    UserId = userId.Value,
                    TaskId = existingByClientAttempt.TaskId,
                    Solved = existingByClientAttempt.IsCorrect,
                    BestScore = existingByClientAttempt.Score,
                    BestScorePercent = existingByClientAttempt.MaxScore <= 0 ? 0 : existingByClientAttempt.Score / existingByClientAttempt.MaxScore * 100m,
                    AttemptsCount = 1,
                    LastAttemptId = existingByClientAttempt.Id,
                    FirstSolvedAt = existingByClientAttempt.IsCorrect ? existingByClientAttempt.CreatedAt : null,
                    LastAttemptAt = existingByClientAttempt.CreatedAt
                };

                return Results.Ok(ToResult(existingByClientAttempt, existingVersion.ExplanationJson, existingProgress));
            }

            var task = await db.Tasks.FirstOrDefaultAsync(x => x.Id == taskId && x.IsPublished);
            if (task == null) return Results.NotFound();

            var version = await db.TaskVersions
                .Where(x => x.TaskId == task.Id && x.VersionNumber == task.CurrentVersion)
                .FirstOrDefaultAsync();
            if (version == null) return Results.NotFound(new { message = "Task version not found" });

            var isCorrect = QuizAnswerChecker.IsCorrect(req.Answer, version.CorrectAnswerJson);
            var score = isCorrect ? 1m : 0m;
            var now = DateTime.UtcNow;

            var oldAttempts = await db.Attempts
                .Where(x => x.UserId == userId.Value && x.TaskId == task.Id)
                .ToListAsync();
            if (oldAttempts.Count > 0)
            {
                db.Attempts.RemoveRange(oldAttempts);
            }

            var attempt = new QuizAttempt
            {
                TaskId = task.Id,
                TaskVersionId = version.Id,
                UserId = userId.Value,
                ClientAttemptId = clientAttemptId,
                AnswerJson = req.Answer.GetRawText(),
                IsCorrect = isCorrect,
                Score = score,
                MaxScore = 1m,
                TimeSpentSeconds = req.TimeSpentSeconds,
                CreatedAt = now
            };

            db.Attempts.Add(attempt);

            var progress = await db.Progress.FirstOrDefaultAsync(x => x.UserId == userId.Value && x.TaskId == task.Id);
            if (progress == null)
            {
                progress = new QuizProgress
                {
                    UserId = userId.Value,
                    TaskId = task.Id
                };
                db.Progress.Add(progress);
            }

            progress.Solved = isCorrect;
            progress.BestScore = score;
            progress.BestScorePercent = score * 100m;
            progress.AttemptsCount = 1;
            progress.LastAttemptId = attempt.Id;
            progress.FirstSolvedAt = isCorrect ? now : null;
            progress.LastAttemptAt = now;

            await db.SaveChangesAsync();
            return Results.Ok(ToResult(attempt, version.ExplanationJson, progress));
        });

        app.MapGet("/api/quiz/me/solutions", [Authorize] async (QuizDbContext db, ClaimsPrincipal user, string? sectionCode) =>
        {
            var userId = TryGetUserId(user);
            if (!userId.HasValue) return Results.Unauthorized();

            var taskQuery = db.Tasks.AsNoTracking().Where(x => x.IsPublished).AsQueryable();
            if (!string.IsNullOrWhiteSpace(sectionCode))
            {
                var normalizedSectionCode = NormalizeSectionCode(sectionCode);
                taskQuery = taskQuery.Where(x => x.SectionCode == normalizedSectionCode);
            }

            var tasks = await taskQuery.ToListAsync();
            var taskIds = tasks.Select(x => x.Id).ToList();
            if (taskIds.Count == 0) return Results.Ok(Array.Empty<QuizSolutionDto>());

            var attempts = await db.Attempts.AsNoTracking()
                .Where(x => x.UserId == userId.Value && taskIds.Contains(x.TaskId))
                .OrderByDescending(x => x.CreatedAt)
                .ToListAsync();

            var latestAttempts = attempts
                .GroupBy(x => x.TaskId)
                .Select(x => x.First())
                .OrderByDescending(x => x.CreatedAt)
                .ToList();

            if (latestAttempts.Count == 0) return Results.Ok(Array.Empty<QuizSolutionDto>());

            var versionIds = latestAttempts.Select(x => x.TaskVersionId).Distinct().ToList();
            var versions = await db.TaskVersions.AsNoTracking()
                .Where(x => versionIds.Contains(x.Id))
                .ToDictionaryAsync(x => x.Id);

            var progressByTask = await db.Progress.AsNoTracking()
                .Where(x => x.UserId == userId.Value && taskIds.Contains(x.TaskId))
                .ToDictionaryAsync(x => x.TaskId);

            var tasksById = tasks.ToDictionary(x => x.Id);
            var result = new List<QuizSolutionDto>();

            foreach (var attempt in latestAttempts)
            {
                if (!tasksById.TryGetValue(attempt.TaskId, out var task)) continue;
                if (!versions.TryGetValue(attempt.TaskVersionId, out var version)) continue;
                progressByTask.TryGetValue(attempt.TaskId, out var progress);

                var percent = attempt.MaxScore <= 0 ? 0 : Math.Round(attempt.Score / attempt.MaxScore * 100m, 2);
                result.Add(new QuizSolutionDto(
                    QuizTaskDto.FromEntity(task),
                    attempt.Id,
                    attempt.TaskVersionId,
                    attempt.AnswerJson,
                    attempt.IsCorrect,
                    attempt.Score,
                    attempt.MaxScore,
                    percent,
                    version.ExplanationJson,
                    attempt.CreatedAt,
                    progress == null ? null : QuizProgressDto.FromEntity(progress)));
            }

            return Results.Ok(result);
        });

        app.MapGet("/api/quiz/me/progress", [Authorize] async (QuizDbContext db, ClaimsPrincipal user, string? sectionCode) =>
        {
            var userId = TryGetUserId(user);
            if (!userId.HasValue) return Results.Unauthorized();

            var query = db.Progress.AsNoTracking().Where(x => x.UserId == userId.Value);
            if (!string.IsNullOrWhiteSpace(sectionCode))
            {
                var normalizedSectionCode = NormalizeSectionCode(sectionCode);
                var taskIds = await db.Tasks.AsNoTracking()
                    .Where(x => x.SectionCode == normalizedSectionCode)
                    .Select(x => x.Id)
                    .ToListAsync();
                query = query.Where(x => taskIds.Contains(x.TaskId));
            }

            var progressEntities = await query
                .OrderByDescending(x => x.LastAttemptAt)
                .ToListAsync();

            return Results.Ok(progressEntities.Select(QuizProgressDto.FromEntity).ToList());
        });

        return app;
    }
}
