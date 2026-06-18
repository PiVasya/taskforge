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
    private static WebApplication MapLearningEndpoints(WebApplication app)
    {
        app.MapGet("/api/learning/courses/tree", async (LearningDbContext db, ClaimsPrincipal user, bool includeDraft = false) =>
        {
            includeDraft = includeDraft && CanEditLearning(user);
            var query = db.Courses.AsNoTracking();
            if (!includeDraft) query = query.Where(x => x.IsPublished);

            var courses = await query.OrderBy(x => x.SortOrder).ThenBy(x => x.Title).ToListAsync();
            var byParent = courses
                .Where(x => x.ParentCourseId.HasValue)
                .GroupBy(x => x.ParentCourseId!.Value)
                .ToDictionary(x => x.Key, x => x.ToList());

            LearningCourseTreeDto Map(LearningCourse c)
            {
                var dto = new LearningCourseTreeDto
                {
                    Id = c.Id,
                    ParentCourseId = c.ParentCourseId,
                    Slug = c.Slug,
                    Title = c.Title,
                    ShortTitle = c.ShortTitle,
                    Summary = c.Summary,
                    Description = c.Description,
                    SubjectCode = c.SubjectCode,
                    ExamCode = c.ExamCode,
                    SectionCode = c.SectionCode,
                    SortOrder = c.SortOrder
                };

                if (byParent.TryGetValue(c.Id, out var children))
                {
                    dto.Children = children.Select(Map).ToList();
                }

                return dto;
            }

            var roots = courses
                .Where(x => !x.ParentCourseId.HasValue)
                .Select(Map)
                .ToList();

            return Microsoft.AspNetCore.Http.Results.Ok(roots);
        });

        app.MapGet("/api/learning/courses/{slug}/outline", async (LearningDbContext db, ClaimsPrincipal user, string slug, bool includeDraft = false) =>
        {
            includeDraft = includeDraft && CanEditLearning(user);
            var course = await db.Courses.AsNoTracking().FirstOrDefaultAsync(x => x.Slug == slug);
            if (course == null || (!includeDraft && !course.IsPublished)) return ApiError(StatusCodes.Status404NotFound, "Курс не найден.", $"Slug: {slug}", "Проверь выбранный узел дерева или включи includeDraft=true для черновиков.");

            var childrenQuery = db.Courses.AsNoTracking().Where(x => x.ParentCourseId == course.Id);
            var pagesQuery = db.Pages.AsNoTracking().Where(x => x.CourseId == course.Id);
            var conspectsQuery = db.Conspects.AsNoTracking().Where(x => x.CourseId == course.Id);

            if (!includeDraft)
            {
                childrenQuery = childrenQuery.Where(x => x.IsPublished);
                pagesQuery = pagesQuery.Where(x => x.IsPublished);
                conspectsQuery = conspectsQuery.Where(x => x.IsPublished);
            }

            var childEntities = await childrenQuery.OrderBy(x => x.SortOrder).ThenBy(x => x.Title).ToListAsync();
            var pageEntities = await pagesQuery.OrderBy(x => x.SortOrder).ThenBy(x => x.Title).ToListAsync();
            var conspectEntities = await conspectsQuery.OrderBy(x => x.SortOrder).ThenBy(x => x.Title).ToListAsync();
            var taskEntities = await db.CourseTaskLinks.AsNoTracking()
                .Where(x => x.CourseId == course.Id)
                .OrderBy(x => x.SortOrder)
                .ThenBy(x => x.Title)
                .ToListAsync();

            var children = childEntities.Select(LearningCourseDto.FromEntity).ToList();
            var pages = pageEntities.Select(LearningPageDto.FromEntity).ToList();
            var conspects = conspectEntities.Select(LearningConspectDto.FromEntity).ToList();
            var tasks = taskEntities.Select(LearningTaskLinkDto.FromEntity).ToList();

            return Microsoft.AspNetCore.Http.Results.Ok(new LearningCourseOutlineDto(LearningCourseDto.FromEntity(course), children, pages, conspects, tasks));
        });

        app.MapGet("/api/learning/courses/{slug}/conspects", async (LearningDbContext db, ClaimsPrincipal user, string slug, bool includeDraft = false) =>
        {
            includeDraft = includeDraft && CanEditLearning(user);
            var course = await db.Courses.AsNoTracking().FirstOrDefaultAsync(x => x.Slug == slug);
            if (course == null || (!includeDraft && !course.IsPublished)) return ApiError(StatusCodes.Status404NotFound, "Курс не найден.", $"Slug: {slug}", "Проверь выбранный узел дерева или включи includeDraft=true для черновиков.");

            var query = db.Conspects.AsNoTracking().Where(x => x.CourseId == course.Id);
            if (!includeDraft) query = query.Where(x => x.IsPublished);

            var items = await query.OrderBy(x => x.SortOrder).ThenBy(x => x.Title).ToListAsync();
            return Microsoft.AspNetCore.Http.Results.Ok(items.Select(LearningConspectDto.FromEntity).ToList());
        });

        app.MapGet("/api/learning/conspects", async (
            LearningDbContext db,
            ClaimsPrincipal user,
            string? subjectCode,
            string? examCode,
            string? sectionCode,
            string? courseSlug,
            bool includeDraft = false) =>
        {
            includeDraft = includeDraft && CanEditLearning(user);
            var query = db.Conspects.AsNoTracking().AsQueryable();
            if (!includeDraft) query = query.Where(x => x.IsPublished);
            if (!string.IsNullOrWhiteSpace(subjectCode)) query = query.Where(x => x.SubjectCode == subjectCode);
            if (!string.IsNullOrWhiteSpace(examCode)) query = query.Where(x => x.ExamCode == examCode);
            if (!string.IsNullOrWhiteSpace(sectionCode)) query = query.Where(x => x.SectionCode == sectionCode);

            if (!string.IsNullOrWhiteSpace(courseSlug))
            {
                var courseId = await db.Courses.AsNoTracking()
                    .Where(x => x.Slug == courseSlug)
                    .Select(x => (Guid?)x.Id)
                    .FirstOrDefaultAsync();
                if (!courseId.HasValue) return Microsoft.AspNetCore.Http.Results.Ok(Array.Empty<LearningConspectDto>());
                query = query.Where(x => x.CourseId == courseId.Value);
            }

            var items = await query.OrderBy(x => x.SortOrder).ThenBy(x => x.Title).ToListAsync();
            return Microsoft.AspNetCore.Http.Results.Ok(items.Select(LearningConspectDto.FromEntity).ToList());
        });

        app.MapGet("/api/learning/conspects/{idOrSlug}", async (LearningDbContext db, ClaimsPrincipal user, string idOrSlug, string? courseSlug, bool includeDraft = false) =>
        {
            includeDraft = includeDraft && CanEditLearning(user);
            var isGuid = Guid.TryParse(idOrSlug, out var id);
            var query = db.Conspects.AsNoTracking().AsQueryable();

            if (isGuid) query = query.Where(x => x.Id == id);
            else query = query.Where(x => x.Slug == idOrSlug);

            if (!includeDraft) query = query.Where(x => x.IsPublished);

            if (!string.IsNullOrWhiteSpace(courseSlug))
            {
                var courseId = await db.Courses.AsNoTracking()
                    .Where(x => x.Slug == courseSlug)
                    .Select(x => (Guid?)x.Id)
                    .FirstOrDefaultAsync();
                if (!courseId.HasValue) return ApiError(StatusCodes.Status404NotFound, "Курс для конспекта не найден.", $"courseSlug: {courseSlug}", "Обнови дерево редактора и попробуй снова.");
                query = query.Where(x => x.CourseId == courseId.Value);
            }

            var conspect = await query.OrderBy(x => x.SortOrder).FirstOrDefaultAsync();
            if (conspect == null) return ApiError(StatusCodes.Status404NotFound, "Конспект не найден.", $"idOrSlug: {idOrSlug}", "Проверь slug конспекта или открой его из списка в редакторе.");

            var taskLinks = await db.ConspectTaskLinks.AsNoTracking()
                .Where(x => x.ConspectId == conspect.Id)
                .OrderBy(x => x.SortOrder)
                .ThenBy(x => x.Title)
                .ToListAsync();

            return Microsoft.AspNetCore.Http.Results.Ok(new LearningConspectDetailsDto(
                LearningConspectDto.FromEntity(conspect),
                conspect.ContentJson,
                conspect.SearchText,
                taskLinks.Select(LearningConspectTaskLinkDto.FromEntity).ToList()));
        });

        return app;
    }
}
