using System.Security.Claims;
using System.Text;
using System.Text.Json;
using LearningContentService.Data;
using LearningContentService.Data.Entities;
using LearningContentService.DTO;
using LearningContentService.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using static LearningContentService.Services.Common.LearningContentCommonService;
using static LearningContentService.Services.Serialization.LearningContentSerializationService;

namespace LearningContentService.Endpoints;

internal static partial class LearningContentEndpoints
{
    private static WebApplication MapAdminLearningEndpoints(WebApplication app)
    {
        app.MapPost("/api/admin/learning/courses", [Authorize(Roles = "Admin,LearningEditor")] async (LearningDbContext db, [FromBody] CreateLearningCourseRequest req) =>
        {
            if (string.IsNullOrWhiteSpace(req.Slug) || string.IsNullOrWhiteSpace(req.Title))
            {
                return ApiError(StatusCodes.Status400BadRequest, "Нужно заполнить Slug и Title.", null, "Slug — это часть URL, Title — название в интерфейсе.");
            }

            if (req.ParentCourseId.HasValue)
            {
                var parentExists = await db.Courses.AnyAsync(x => x.Id == req.ParentCourseId.Value);
                if (!parentExists) return ApiError(StatusCodes.Status404NotFound, "Родительский курс не найден.", $"parentCourseId: {req.ParentCourseId}", "Обнови дерево редактора и выбери родителя заново.");
            }

            var exists = await db.Courses.AnyAsync(x => x.Slug == req.Slug.Trim());
            if (exists) return ApiError(StatusCodes.Status409Conflict, "Курс с таким slug уже существует.", req.Slug.Trim(), "Выбери другой slug, например добавь -2 или код раздела.");

            var course = new LearningCourse
            {
                ParentCourseId = req.ParentCourseId,
                Slug = req.Slug.Trim(),
                Title = req.Title.Trim(),
                ShortTitle = req.ShortTitle?.Trim(),
                Summary = req.Summary,
                Description = req.Description,
                SubjectCode = req.SubjectCode,
                ExamCode = req.ExamCode,
                SectionCode = req.SectionCode,
                SortOrder = req.SortOrder,
                IsPublished = req.IsPublished
            };

            db.Courses.Add(course);
            await db.SaveChangesAsync();
            return Results.Ok(LearningCourseDto.FromEntity(course));
        });

        app.MapPut("/api/admin/learning/courses/{id:guid}", [Authorize(Roles = "Admin,LearningEditor")] async (LearningDbContext db, Guid id, [FromBody] UpdateLearningCourseRequest req) =>
        {
            var course = await db.Courses.FirstOrDefaultAsync(x => x.Id == id);
            if (course == null) return ApiError(StatusCodes.Status404NotFound, "Курс / раздел не найден.", $"Id: {id}", "Обнови страницу редактора: возможно, объект был удалён или выбран старый id.");

            if (!string.IsNullOrWhiteSpace(req.Slug))
            {
                var newSlug = req.Slug.Trim();
                var duplicate = await db.Courses.AnyAsync(x => x.Id != id && x.Slug == newSlug);
                if (duplicate) return ApiError(StatusCodes.Status409Conflict, "Другой курс уже использует такой slug.", newSlug, "Slug должен быть уникальным во всём learning-дереве.");
                course.Slug = newSlug;
            }
            if (!string.IsNullOrWhiteSpace(req.Title)) course.Title = req.Title.Trim();
            if (req.ParentCourseId.HasValue) course.ParentCourseId = req.ParentCourseId;
            if (req.ShortTitle != null) course.ShortTitle = req.ShortTitle.Trim();
            if (req.Summary != null) course.Summary = req.Summary;
            if (req.Description != null) course.Description = req.Description;
            if (req.SubjectCode != null) course.SubjectCode = req.SubjectCode;
            if (req.ExamCode != null) course.ExamCode = req.ExamCode;
            if (req.SectionCode != null) course.SectionCode = req.SectionCode;
            if (req.SortOrder.HasValue) course.SortOrder = req.SortOrder.Value;
            if (req.IsPublished.HasValue) course.IsPublished = req.IsPublished.Value;
            course.UpdatedAt = DateTime.UtcNow;

            await db.SaveChangesAsync();
            return Results.Ok(LearningCourseDto.FromEntity(course));
        });

        app.MapDelete("/api/admin/learning/courses/{id:guid}", [Authorize(Roles = "Admin,LearningEditor")] async (LearningDbContext db, Guid id) =>
        {
            var course = await db.Courses.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
            if (course == null) return ApiError(StatusCodes.Status404NotFound, "Раздел не найден.", $"Id: {id}", "Обнови страницу редактора: возможно, раздел уже удалён.");
            if (string.IsNullOrWhiteSpace(course.SectionCode))
            {
                return ApiError(StatusCodes.Status400BadRequest, "Нельзя удалить корневой курс через эту кнопку.", course.Title, "Удалять полностью можно только конкретные номера A/B, у которых заполнен SectionCode.");
            }

            var allCourses = await db.Courses.AsNoTracking().ToListAsync();
            var ids = new HashSet<Guid> { id };
            var changed = true;
            while (changed)
            {
                changed = false;
                foreach (var child in allCourses.Where(x => x.ParentCourseId.HasValue && ids.Contains(x.ParentCourseId.Value)))
                {
                    if (ids.Add(child.Id)) changed = true;
                }
            }

            var courseIds = ids.ToList();
            var conspects = await db.Conspects.Where(x => courseIds.Contains(x.CourseId)).ToListAsync();
            var conspectIds = conspects.Select(x => x.Id).ToList();
            var pages = await db.Pages.Where(x => courseIds.Contains(x.CourseId)).ToListAsync();
            var courseTaskLinks = await db.CourseTaskLinks.Where(x => courseIds.Contains(x.CourseId)).ToListAsync();
            var conspectTaskLinks = conspectIds.Count == 0
                ? new List<LearningConspectTaskLink>()
                : await db.ConspectTaskLinks.Where(x => conspectIds.Contains(x.ConspectId)).ToListAsync();
            var courses = await db.Courses.Where(x => courseIds.Contains(x.Id)).ToListAsync();

            db.ConspectTaskLinks.RemoveRange(conspectTaskLinks);
            db.CourseTaskLinks.RemoveRange(courseTaskLinks);
            db.Pages.RemoveRange(pages);
            db.Conspects.RemoveRange(conspects);
            db.Courses.RemoveRange(courses);
            await db.SaveChangesAsync();

            return Results.Ok(new
            {
                courseIdDeleted = id,
                sectionCode = course.SectionCode,
                coursesDeleted = courses.Count,
                conspectsDeleted = conspects.Count,
                pagesDeleted = pages.Count,
                courseTaskLinksDeleted = courseTaskLinks.Count,
                conspectTaskLinksDeleted = conspectTaskLinks.Count
            });
        });

        app.MapPost("/api/admin/learning/courses/{courseId:guid}/pages", [Authorize(Roles = "Admin,LearningEditor")] async (LearningDbContext db, Guid courseId, [FromBody] CreateLearningPageRequest req) =>
        {
            var courseExists = await db.Courses.AnyAsync(x => x.Id == courseId);
            if (!courseExists) return ApiError(StatusCodes.Status404NotFound, "Курс не найден.", $"courseId: {courseId}");

            var bodyJson = JsonOrDefault(req.Body, req.BodyJson, null);

            var page = new LearningPage
            {
                CourseId = courseId,
                Slug = req.Slug.Trim(),
                Title = req.Title.Trim(),
                Kind = string.IsNullOrWhiteSpace(req.Kind) ? "theory" : req.Kind.Trim(),
                BodyMarkdown = req.BodyMarkdown,
                BodyJson = bodyJson,
                SortOrder = req.SortOrder,
                IsPublished = req.IsPublished
            };

            db.Pages.Add(page);
            await db.SaveChangesAsync();
            return Results.Ok(LearningPageDto.FromEntity(page));
        });

        app.MapPost("/api/admin/learning/courses/{courseId:guid}/conspects", [Authorize(Roles = "Admin,LearningEditor")] async (LearningDbContext db, Guid courseId, [FromBody] CreateLearningConspectRequest req) =>
        {
            var course = await db.Courses.AsNoTracking().FirstOrDefaultAsync(x => x.Id == courseId);
            if (course == null) return ApiError(StatusCodes.Status404NotFound, "Курс не найден.", $"courseId: {courseId}", "Обнови дерево редактора и выбери курс заново.");
            if (string.IsNullOrWhiteSpace(req.Slug) || string.IsNullOrWhiteSpace(req.Title))
            {
                return ApiError(StatusCodes.Status400BadRequest, "Нужно заполнить Slug и Title.", null, "Slug — это часть URL, Title — название в интерфейсе.");
            }

            var exists = await db.Conspects.AnyAsync(x => x.CourseId == courseId && x.Slug == req.Slug.Trim());
            if (exists) return ApiError(StatusCodes.Status409Conflict, "В этом курсе уже есть конспект с таким slug.", req.Slug.Trim(), "Открой существующий конспект из списка или задай новый slug.");

            var conspect = new LearningConspect
            {
                CourseId = courseId,
                Slug = req.Slug.Trim(),
                Title = req.Title.Trim(),
                Subtitle = req.Subtitle?.Trim(),
                Lead = req.Lead,
                SubjectCode = string.IsNullOrWhiteSpace(req.SubjectCode) ? course.SubjectCode : req.SubjectCode.Trim(),
                ExamCode = string.IsNullOrWhiteSpace(req.ExamCode) ? course.ExamCode : req.ExamCode.Trim(),
                SectionCode = string.IsNullOrWhiteSpace(req.SectionCode) ? course.SectionCode : req.SectionCode.Trim(),
                Kind = string.IsNullOrWhiteSpace(req.Kind) ? "conspect" : req.Kind.Trim(),
                SortOrder = req.SortOrder,
                EstimatedMinutes = req.EstimatedMinutes <= 0 ? 10 : req.EstimatedMinutes,
                BadgesJson = JsonOrDefault(req.Badges, req.BadgesJson, "[]"),
                ContentJson = JsonOrDefault(req.Content, req.ContentJson, "{}") ?? "{}",
                SearchText = req.SearchText,
                IsPublished = req.IsPublished
            };

            db.Conspects.Add(conspect);
            await db.SaveChangesAsync();
            return Results.Ok(new LearningConspectDetailsDto(LearningConspectDto.FromEntity(conspect), conspect.ContentJson, conspect.SearchText, Array.Empty<LearningConspectTaskLinkDto>()));
        });

        app.MapPut("/api/admin/learning/conspects/{id:guid}", [Authorize(Roles = "Admin,LearningEditor")] async (LearningDbContext db, Guid id, [FromBody] UpdateLearningConspectRequest req) =>
        {
            var conspect = await db.Conspects.FirstOrDefaultAsync(x => x.Id == id);
            if (conspect == null) return ApiError(StatusCodes.Status404NotFound, "Конспект не найден.", $"Id: {id}", "Обнови список конспектов и выбери его заново.");

            if (!string.IsNullOrWhiteSpace(req.Slug))
            {
                var newSlug = req.Slug.Trim();
                var duplicate = await db.Conspects.AnyAsync(x => x.Id != id && x.CourseId == conspect.CourseId && x.Slug == newSlug);
                if (duplicate) return ApiError(StatusCodes.Status409Conflict, "В этом курсе уже есть другой конспект с таким slug.", newSlug, "Открой существующий конспект или задай новый slug.");
                conspect.Slug = newSlug;
            }
            if (!string.IsNullOrWhiteSpace(req.Title)) conspect.Title = req.Title.Trim();
            if (req.Subtitle != null) conspect.Subtitle = req.Subtitle.Trim();
            if (req.Lead != null) conspect.Lead = req.Lead;
            if (req.SubjectCode != null) conspect.SubjectCode = req.SubjectCode.Trim();
            if (req.ExamCode != null) conspect.ExamCode = req.ExamCode.Trim();
            if (req.SectionCode != null) conspect.SectionCode = req.SectionCode.Trim();
            if (req.Kind != null) conspect.Kind = string.IsNullOrWhiteSpace(req.Kind) ? "conspect" : req.Kind.Trim();
            if (req.SortOrder.HasValue) conspect.SortOrder = req.SortOrder.Value;
            if (req.EstimatedMinutes.HasValue) conspect.EstimatedMinutes = req.EstimatedMinutes.Value <= 0 ? 10 : req.EstimatedMinutes.Value;
            if (req.Badges.HasValue || req.BadgesJson != null) conspect.BadgesJson = JsonOrDefault(req.Badges, req.BadgesJson, "[]");
            if (req.Content.HasValue || req.ContentJson != null) conspect.ContentJson = JsonOrDefault(req.Content, req.ContentJson, "{}") ?? "{}";
            if (req.SearchText != null) conspect.SearchText = req.SearchText;
            if (req.IsPublished.HasValue) conspect.IsPublished = req.IsPublished.Value;
            conspect.UpdatedAt = DateTime.UtcNow;

            await db.SaveChangesAsync();

            var taskLinks = await db.ConspectTaskLinks.AsNoTracking()
                .Where(x => x.ConspectId == conspect.Id)
                .OrderBy(x => x.SortOrder)
                .ThenBy(x => x.Title)
                .ToListAsync();

            return Results.Ok(new LearningConspectDetailsDto(
                LearningConspectDto.FromEntity(conspect),
                conspect.ContentJson,
                conspect.SearchText,
                taskLinks.Select(LearningConspectTaskLinkDto.FromEntity).ToList()));
        });

        app.MapPost("/api/admin/learning/conspects/{conspectId:guid}/task-links", [Authorize(Roles = "Admin,LearningEditor")] async (LearningDbContext db, Guid conspectId, [FromBody] CreateLearningConspectTaskLinkRequest req) =>
        {
            var conspectExists = await db.Conspects.AnyAsync(x => x.Id == conspectId);
            if (!conspectExists) return ApiError(StatusCodes.Status404NotFound, "Конспект не найден.", $"conspectId: {conspectId}", "Сначала сохрани конспект, затем добавляй связи с заданиями.");
            if (string.IsNullOrWhiteSpace(req.Title)) return ApiError(StatusCodes.Status400BadRequest, "Нужно заполнить название связи с заданием.");

            var link = new LearningConspectTaskLink
            {
                ConspectId = conspectId,
                TaskId = req.TaskId,
                TaskType = string.IsNullOrWhiteSpace(req.TaskType) ? "quiz-mini" : req.TaskType.Trim(),
                SourceService = string.IsNullOrWhiteSpace(req.SourceService) ? "quiz-task-service" : req.SourceService.Trim(),
                TaskSlug = req.TaskSlug?.Trim(),
                TaskFilterJson = JsonOrDefault(req.TaskFilter, req.TaskFilterJson, null),
                Title = req.Title.Trim(),
                ButtonText = string.IsNullOrWhiteSpace(req.ButtonText) ? "К заданиям" : req.ButtonText.Trim(),
                GroupTitle = req.GroupTitle?.Trim(),
                AnchorBlockId = req.AnchorBlockId?.Trim(),
                IsRequired = req.IsRequired,
                SortOrder = req.SortOrder
            };

            db.ConspectTaskLinks.Add(link);
            await db.SaveChangesAsync();
            return Results.Ok(LearningConspectTaskLinkDto.FromEntity(link));
        });

        app.MapDelete("/api/admin/learning/conspect-task-links/{id:guid}", [Authorize(Roles = "Admin,LearningEditor")] async (LearningDbContext db, Guid id) =>
        {
            var link = await db.ConspectTaskLinks.FirstOrDefaultAsync(x => x.Id == id);
            if (link == null) return ApiError(StatusCodes.Status404NotFound, "Связь с заданием не найдена.", $"Id: {id}");
            db.ConspectTaskLinks.Remove(link);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        app.MapPost("/api/admin/learning/courses/{courseId:guid}/task-links", [Authorize(Roles = "Admin,LearningEditor")] async (LearningDbContext db, Guid courseId, [FromBody] CreateLearningTaskLinkRequest req) =>
        {
            var courseExists = await db.Courses.AnyAsync(x => x.Id == courseId);
            if (!courseExists) return ApiError(StatusCodes.Status404NotFound, "Курс не найден.", $"courseId: {courseId}");

            var link = new LearningCourseTaskLink
            {
                CourseId = courseId,
                TaskId = req.TaskId,
                TaskType = string.IsNullOrWhiteSpace(req.TaskType) ? "quiz-mini" : req.TaskType.Trim(),
                SourceService = string.IsNullOrWhiteSpace(req.SourceService) ? "quiz-task-service" : req.SourceService.Trim(),
                TaskSlug = req.TaskSlug,
                Title = req.Title,
                GroupTitle = req.GroupTitle,
                IsRequired = req.IsRequired,
                SortOrder = req.SortOrder
            };

            db.CourseTaskLinks.Add(link);
            await db.SaveChangesAsync();
            return Results.Ok(LearningTaskLinkDto.FromEntity(link));
        });

        app.MapPost("/api/admin/learning/courses/{courseId}/conspects", [Authorize(Roles = "Admin,LearningEditor")] (string courseId) =>
        {
            return ApiError(StatusCodes.Status400BadRequest, "Некорректный id курса в адресе создания конспекта.", courseId, "Фронт должен отправлять GUID выбранного LearningCourse, а не slug.");
        });

        return app;
    }
}
