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
    private static WebApplication MapAdminQuizEndpoints(WebApplication app)
    {
        app.MapGet("/api/admin/quiz/tasks", [Authorize(Roles = "Admin,LearningEditor")] async (QuizDbContext db, string? subjectCode, string? examCode, string? sectionCode, string? type) =>
        {
            var query = db.Tasks.AsNoTracking().AsQueryable();
            if (!string.IsNullOrWhiteSpace(subjectCode)) query = query.Where(x => x.SubjectCode == subjectCode);
            if (!string.IsNullOrWhiteSpace(examCode)) query = query.Where(x => x.ExamCode == examCode);
            if (!string.IsNullOrWhiteSpace(sectionCode))
            {
                var normalizedSectionCode = NormalizeSectionCode(sectionCode);
                query = query.Where(x => x.SectionCode == normalizedSectionCode);
            }
            if (!string.IsNullOrWhiteSpace(type)) query = query.Where(x => x.Type == type);

            var tasks = await query
                .OrderBy(x => x.SectionCode)
                .ThenBy(x => x.Difficulty)
                .ThenBy(x => x.Title)
                .ToListAsync();

            var versions = await LoadCurrentVersionsAsync(db, tasks);
            return Results.Ok(tasks.Select(task => ToAdminDto(task, versions.GetValueOrDefault(task.Id))).ToList());
        });

        app.MapGet("/api/admin/quiz/tasks/{id:guid}", [Authorize(Roles = "Admin,LearningEditor")] async (QuizDbContext db, Guid id) =>
        {
            var task = await db.Tasks.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
            if (task == null) return Results.NotFound(new { message = "Task not found" });
            var version = await db.TaskVersions.AsNoTracking()
                .Where(x => x.TaskId == task.Id && x.VersionNumber == task.CurrentVersion)
                .FirstOrDefaultAsync();
            if (version == null) return Results.NotFound(new { message = "Task version not found" });
            return Results.Ok(ToAdminDto(task, version));
        });

        app.MapPost("/api/admin/quiz/tasks", [Authorize(Roles = "Admin,LearningEditor")] async (QuizDbContext db, [FromBody] CreateQuizTaskRequest req) =>
        {
            var validation = ValidateTaskRequest(req);
            if (validation != null) return validation;

            var exists = await db.Tasks.AnyAsync(x => x.Slug == req.Slug.Trim());
            if (exists) return Results.Conflict(new { message = "Task slug already exists" });

            var normalizedSectionCode = NormalizeSectionCode(req.SectionCode);
            var explanationJson = JsonOrDefault(req.Explanation, req.ExplanationJson, "{}");
            var task = new QuizTask
            {
                Slug = req.Slug.Trim(),
                Type = NormalizeTaskType(req.Type, normalizedSectionCode),
                Title = req.Title.Trim(),
                Prompt = req.Prompt.Trim(),
                SubjectCode = string.IsNullOrWhiteSpace(req.SubjectCode) ? "russian" : req.SubjectCode.Trim(),
                ExamCode = string.IsNullOrWhiteSpace(req.ExamCode) ? "ct-ce-2026" : req.ExamCode.Trim(),
                SectionCode = normalizedSectionCode,
                Difficulty = req.Difficulty <= 0 ? 1 : req.Difficulty,
                TagsJson = JsonOrDefault(req.Tags, req.TagsJson, "[]"),
                SourceName = req.SourceName,
                SourceYear = req.SourceYear,
                IsPublished = req.IsPublished,
                CurrentVersion = 1
            };

            var version = new QuizTaskVersion
            {
                TaskId = task.Id,
                VersionNumber = 1,
                DataJson = JsonOrDefault(req.Data, req.DataJson, "{}"),
                CorrectAnswerJson = JsonOrDefault(req.CorrectAnswer, req.CorrectAnswerJson, "{}"),
                ExplanationJson = explanationJson,
                ChangeComment = "Initial version"
            };

            db.Tasks.Add(task);
            db.TaskVersions.Add(version);
            await db.SaveChangesAsync();

            return Results.Ok(ToAdminDto(task, version));
        });

        app.MapPut("/api/admin/quiz/tasks/{id:guid}", [Authorize(Roles = "Admin,LearningEditor")] async (QuizDbContext db, Guid id, [FromBody] CreateQuizTaskRequest req) =>
        {
            var validation = ValidateTaskRequest(req);
            if (validation != null) return validation;

            var task = await db.Tasks.FirstOrDefaultAsync(x => x.Id == id);
            if (task == null) return Results.NotFound(new { message = "Task not found" });

            var newSlug = req.Slug.Trim();
            var duplicate = await db.Tasks.AnyAsync(x => x.Id != id && x.Slug == newSlug);
            if (duplicate) return Results.Conflict(new { message = "Task slug already exists" });

            var normalizedSectionCode = NormalizeSectionCode(req.SectionCode);
            task.Slug = newSlug;
            task.Type = NormalizeTaskType(req.Type, normalizedSectionCode);
            task.Title = req.Title.Trim();
            task.Prompt = req.Prompt.Trim();
            task.SubjectCode = string.IsNullOrWhiteSpace(req.SubjectCode) ? "russian" : req.SubjectCode.Trim();
            task.ExamCode = string.IsNullOrWhiteSpace(req.ExamCode) ? "ct-ce-2026" : req.ExamCode.Trim();
            task.SectionCode = normalizedSectionCode;
            task.Difficulty = req.Difficulty <= 0 ? 1 : req.Difficulty;
            task.TagsJson = JsonOrDefault(req.Tags, req.TagsJson, "[]");
            task.SourceName = req.SourceName;
            task.SourceYear = req.SourceYear;
            task.IsPublished = req.IsPublished;
            task.UpdatedAt = DateTime.UtcNow;

            var nextVersionNumber = await db.TaskVersions
                .Where(x => x.TaskId == task.Id)
                .Select(x => (int?)x.VersionNumber)
                .MaxAsync() ?? 0;
            nextVersionNumber += 1;
            task.CurrentVersion = nextVersionNumber;

            var version = new QuizTaskVersion
            {
                TaskId = task.Id,
                VersionNumber = nextVersionNumber,
                DataJson = JsonOrDefault(req.Data, req.DataJson, "{}"),
                CorrectAnswerJson = JsonOrDefault(req.CorrectAnswer, req.CorrectAnswerJson, "{}"),
                ExplanationJson = JsonOrDefault(req.Explanation, req.ExplanationJson, "{}"),
                ChangeComment = "Edited from CT editor"
            };

            db.TaskVersions.Add(version);
            await db.SaveChangesAsync();

            return Results.Ok(ToAdminDto(task, version));
        });

        app.MapDelete("/api/admin/quiz/tasks/by-section", [Authorize(Roles = "Admin,LearningEditor")] async (QuizDbContext db, string? subjectCode, string? examCode, string? sectionCode) =>
        {
            if (string.IsNullOrWhiteSpace(sectionCode))
            {
                return Results.BadRequest(new { message = "sectionCode is required" });
            }

            var normalizedSectionCode = NormalizeSectionCode(sectionCode)!;
            if (!System.Text.RegularExpressions.Regex.IsMatch(normalizedSectionCode, "^[AB][0-9]+$"))
            {
                return Results.BadRequest(new { message = "sectionCode must look like A1, A31, B1 or B11" });
            }

            var query = db.Tasks.Where(x => x.SectionCode == normalizedSectionCode).AsQueryable();
            if (!string.IsNullOrWhiteSpace(subjectCode)) query = query.Where(x => x.SubjectCode == subjectCode.Trim());
            if (!string.IsNullOrWhiteSpace(examCode)) query = query.Where(x => x.ExamCode == examCode.Trim());

            var tasks = await query.ToListAsync();
            var taskIds = tasks.Select(x => x.Id).ToList();
            if (taskIds.Count == 0)
            {
                return Results.Ok(new
                {
                    sectionCode = normalizedSectionCode,
                    tasksDeleted = 0,
                    versionsDeleted = 0,
                    attemptsDeleted = 0,
                    progressDeleted = 0
                });
            }

            var attempts = await db.Attempts.Where(x => taskIds.Contains(x.TaskId)).ToListAsync();
            var progress = await db.Progress.Where(x => taskIds.Contains(x.TaskId)).ToListAsync();
            var versions = await db.TaskVersions.Where(x => taskIds.Contains(x.TaskId)).ToListAsync();

            db.Attempts.RemoveRange(attempts);
            db.Progress.RemoveRange(progress);
            db.TaskVersions.RemoveRange(versions);
            db.Tasks.RemoveRange(tasks);
            await db.SaveChangesAsync();

            return Results.Ok(new
            {
                sectionCode = normalizedSectionCode,
                tasksDeleted = tasks.Count,
                versionsDeleted = versions.Count,
                attemptsDeleted = attempts.Count,
                progressDeleted = progress.Count
            });
        });

        app.MapDelete("/api/admin/quiz/tasks/{id:guid}", [Authorize(Roles = "Admin,LearningEditor")] async (QuizDbContext db, Guid id) =>
        {
            var task = await db.Tasks.FirstOrDefaultAsync(x => x.Id == id);
            if (task == null) return Results.NotFound(new { message = "Task not found" });

            var attempts = await db.Attempts.Where(x => x.TaskId == id).ToListAsync();
            var progress = await db.Progress.Where(x => x.TaskId == id).ToListAsync();
            var versions = await db.TaskVersions.Where(x => x.TaskId == id).ToListAsync();

            db.Attempts.RemoveRange(attempts);
            db.Progress.RemoveRange(progress);
            db.TaskVersions.RemoveRange(versions);
            db.Tasks.Remove(task);
            await db.SaveChangesAsync();

            return Results.NoContent();
        });

        return app;
    }
}
