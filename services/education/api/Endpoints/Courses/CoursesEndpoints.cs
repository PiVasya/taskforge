using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using TaskForge.Education.Api.Data;
using TaskForge.Education.Api.Domain;

using TaskForge.Education.Api.Contracts;
using static TaskForge.Education.Api.Services.Access.EducationApiAccessService;
using static TaskForge.Education.Api.Services.Common.EducationApiCommonService;
using static TaskForge.Education.Api.Services.Mapping.EducationApiMappingService;
using static TaskForge.Education.Api.Services.Serialization.EducationApiSerializationService;

namespace TaskForge.Education.Api.Endpoints;

internal static partial class EducationApiEndpoints
{
    private static WebApplication MapCoursesEndpoints(WebApplication app)
    {
        app.MapGet("/api/courses", async (HttpContext http, EducationDbContext db, IConfiguration cfg, int? page, int? pageSize, string? q, bool? tree, CancellationToken ct) =>
        {
            var access = await ResolveAccessContext(http, cfg, db, ct);
            if (!access.UserId.HasValue) return Microsoft.AspNetCore.Http.Results.Unauthorized();

            var normalizedQuery = string.Join(' ', (q ?? string.Empty).Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            var requestedPagedShape = tree == true ? false : page.HasValue || pageSize.HasValue || !string.IsNullOrWhiteSpace(normalizedQuery);
            var size = System.Math.Clamp(pageSize ?? 12, 1, 50);
            var currentPage = System.Math.Max(1, page ?? 1);

            var rows = await db.Courses.AsNoTracking()
                .OrderBy(x => x.ParentCourseId.HasValue)
                .ThenBy(x => x.ParentCourseId)
                .ThenBy(x => x.Sort)
                .ThenBy(x => x.Title)
                .ToListAsync(ct);
            var hiddenCourseIds = BuildHiddenCourseIds(rows);
            var visibleRows = rows.Where(x => CanViewCourse(access, x, hiddenCourseIds.Contains(x.Id)));
            if (!string.IsNullOrWhiteSpace(normalizedQuery))
            {
                visibleRows = visibleRows.Where(x =>
                    x.Title.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase)
                    || (!string.IsNullOrWhiteSpace(x.Description) && x.Description.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase)));
            }

            var visible = visibleRows.ToList();
            if (!requestedPagedShape)
            {
                return Microsoft.AspNetCore.Http.Results.Ok(visible.Select(x => ToCourseDto(x, CanEditCourse(access, x))).ToList());
            }

            var total = visible.Count;
            var pageRows = visible.Skip((currentPage - 1) * size).Take(size).Select(x => ToCourseDto(x, CanEditCourse(access, x))).ToList();
            return Microsoft.AspNetCore.Http.Results.Ok(new PagedResult<CourseDto>(pageRows, currentPage, size, total, currentPage * size < total));
        });

        app.MapPost("/api/courses", async (CourseRequest request, HttpContext http, IConfiguration cfg, EducationDbContext db, CancellationToken ct) =>
        {
            var currentUserId = TaskForgeRequestSecurity.UserId(http, cfg);
            var ownerIds = request.OwnerIds?.Where(x => x != Guid.Empty).Distinct().ToArray();
            if ((ownerIds == null || ownerIds.Length == 0) && currentUserId.HasValue)
            {
                ownerIds = new[] { currentUserId.Value };
            }

            var parentId = request.ParentCourseId == Guid.Empty ? null : request.ParentCourseId;
            if (parentId.HasValue && !await db.Courses.AsNoTracking().AnyAsync(x => x.Id == parentId.Value, ct))
            {
                return Microsoft.AspNetCore.Http.Results.Json(new { message = "Родительский курс не найден.", code = "COURSE_PARENT_NOT_FOUND" }, statusCode: StatusCodes.Status400BadRequest);
            }

            var maxSort = await db.Courses.Where(x => x.ParentCourseId == parentId).Select(x => (int?)x.Sort).MaxAsync(ct) ?? -1;
            var course = new Course
            {
                Title = string.IsNullOrWhiteSpace(request.Title) ? "Новый курс" : request.Title.Trim(),
                Description = request.Description,
                IsPublic = request.IsPublic ?? false,
                IsHiddenFromStudents = request.IsHiddenFromStudents ?? false,
                ParentCourseId = parentId,
                Sort = request.Sort.HasValue ? System.Math.Max(0, request.Sort.Value) : maxSort + 1,
                OwnerIdsJson = Serialize(ownerIds),
                VisibleGroupIdsJson = Serialize(request.VisibleGroupIds)
            };
            db.Courses.Add(course);
            await db.SaveChangesAsync(ct);
            return Microsoft.AspNetCore.Http.Results.Ok(ToCourseDto(course, canEdit: true));
        });

        app.MapGet("/api/courses/{id:guid}", async (Guid id, HttpContext http, EducationDbContext db, IConfiguration cfg, CancellationToken ct) =>
        {
            var access = await ResolveAccessContext(http, cfg, db, ct);
            if (!access.UserId.HasValue) return Microsoft.AspNetCore.Http.Results.Unauthorized();

            var rows = await db.Courses.AsNoTracking().ToListAsync(ct);
            var course = rows.FirstOrDefault(x => x.Id == id);
            var hiddenCourseIds = BuildHiddenCourseIds(rows);
            if (course == null || !CanViewCourse(access, course, hiddenCourseIds.Contains(course.Id))) return Microsoft.AspNetCore.Http.Results.NotFound();

            return Microsoft.AspNetCore.Http.Results.Ok(ToCourseDto(course, CanEditCourse(access, course)));
        });

        app.MapPut("/api/courses/{id:guid}", async (Guid id, CourseRequest request, EducationDbContext db, CancellationToken ct) =>
        {
            var course = await db.Courses.FindAsync(new object[] { id }, ct);
            if (course == null) return Microsoft.AspNetCore.Http.Results.NotFound();
            if (!string.IsNullOrWhiteSpace(request.Title)) course.Title = request.Title.Trim();
            course.Description = request.Description;
            if (request.IsPublic.HasValue) course.IsPublic = request.IsPublic.Value;
            if (request.IsHiddenFromStudents.HasValue) course.IsHiddenFromStudents = request.IsHiddenFromStudents.Value;
            if (request.Sort.HasValue) course.Sort = System.Math.Max(0, request.Sort.Value);
            if (request.OwnerIds != null) course.OwnerIdsJson = Serialize(request.OwnerIds);
            if (request.VisibleGroupIds != null) course.VisibleGroupIdsJson = Serialize(request.VisibleGroupIds);
            course.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            return Microsoft.AspNetCore.Http.Results.Ok(ToCourseDto(course, canEdit: true));
        });

        app.MapDelete("/api/courses/{id:guid}", async (Guid id, EducationDbContext db, CancellationToken ct) =>
        {
            var course = await db.Courses.FindAsync(new object[] { id }, ct);
            if (course == null) return Microsoft.AspNetCore.Http.Results.NotFound();

            var children = await db.Courses.Where(x => x.ParentCourseId == id).ToListAsync(ct);
            foreach (var child in children)
            {
                child.ParentCourseId = null;
                child.UpdatedAt = DateTimeOffset.UtcNow;
            }

            db.Courses.Remove(course);
            await db.SaveChangesAsync(ct);
            return Microsoft.AspNetCore.Http.Results.Ok(new { message = "deleted" });
        });

        app.MapPatch("/api/courses/{courseId:guid}/sort", async (Guid courseId, CourseSortRequest request, EducationDbContext db, CancellationToken ct) =>
        {
            var course = await db.Courses.FindAsync(new object[] { courseId }, ct);
            if (course == null) return Microsoft.AspNetCore.Http.Results.NotFound(new { message = "Курс не найден.", code = "COURSE_NOT_FOUND" });
            course.Sort = System.Math.Max(0, request.Sort);
            course.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            return Microsoft.AspNetCore.Http.Results.Ok(ToCourseDto(course, canEdit: true));
        });

        app.MapPatch("/api/courses/{courseId:guid}/position", async (Guid courseId, CoursePositionRequest request, EducationDbContext db, CancellationToken ct) =>
        {
            var course = await db.Courses.FindAsync(new object[] { courseId }, ct);
            if (course == null) return Microsoft.AspNetCore.Http.Results.NotFound(new { message = "Курс не найден.", code = "COURSE_NOT_FOUND" });

            var parentId = request.ParentCourseId == Guid.Empty ? null : request.ParentCourseId;
            if (parentId == course.Id)
            {
                return Microsoft.AspNetCore.Http.Results.Json(new { message = "Курс нельзя вложить сам в себя.", code = "COURSE_PARENT_SELF" }, statusCode: StatusCodes.Status400BadRequest);
            }
            if (parentId.HasValue && !await db.Courses.AsNoTracking().AnyAsync(x => x.Id == parentId.Value, ct))
            {
                return Microsoft.AspNetCore.Http.Results.Json(new { message = "Родительский курс не найден.", code = "COURSE_PARENT_NOT_FOUND" }, statusCode: StatusCodes.Status400BadRequest);
            }
            if (await WouldCreateCourseCycleAsync(course.Id, parentId, db, ct))
            {
                return Microsoft.AspNetCore.Http.Results.Json(new { message = "Такое вложение создаст цикл курсов.", code = "COURSE_PARENT_CYCLE" }, statusCode: StatusCodes.Status400BadRequest);
            }

            var siblings = await db.Courses
                .Where(x => x.ParentCourseId == parentId && x.Id != course.Id)
                .OrderBy(x => x.Sort)
                .ThenBy(x => x.Title)
                .ToListAsync(ct);
            var position = System.Math.Clamp((request.Position ?? siblings.Count + 1) - 1, 0, siblings.Count);

            course.ParentCourseId = parentId;
            course.UpdatedAt = DateTimeOffset.UtcNow;
            siblings.Insert(position, course);
            for (var i = 0; i < siblings.Count; i++)
            {
                siblings[i].Sort = i;
                siblings[i].UpdatedAt = DateTimeOffset.UtcNow;
            }

            await db.SaveChangesAsync(ct);
            return Microsoft.AspNetCore.Http.Results.Ok(ToCourseDto(course, canEdit: true));
        });

        app.MapPost("/api/courses/{courseId:guid}/visible-groups", async (Guid courseId, CourseGroupsRequest request, EducationDbContext db, CancellationToken ct) =>
        {
            var course = await db.Courses.FindAsync(new object[] { courseId }, ct);
            if (course == null) return Microsoft.AspNetCore.Http.Results.NotFound();
            course.VisibleGroupIdsJson = Serialize(request.GroupIds);
            course.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            return Microsoft.AspNetCore.Http.Results.Ok(ToCourseDto(course, canEdit: true));
        });

        app.MapPost("/api/courses/{courseId:guid}/owners", async (Guid courseId, CourseOwnersRequest request, EducationDbContext db, CancellationToken ct) =>
        {
            var course = await db.Courses.FindAsync(new object[] { courseId }, ct);
            if (course == null) return Microsoft.AspNetCore.Http.Results.NotFound();
            course.OwnerIdsJson = Serialize(request.OwnerIds);
            course.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            return Microsoft.AspNetCore.Http.Results.Ok(ToCourseDto(course, canEdit: true));
        });

        return app;
    }

    private static async Task<bool> WouldCreateCourseCycleAsync(Guid courseId, Guid? parentId, EducationDbContext db, CancellationToken ct)
    {
        if (!parentId.HasValue) return false;

        var parents = await db.Courses.AsNoTracking()
            .Where(x => x.ParentCourseId.HasValue)
            .Select(x => new { x.Id, x.ParentCourseId })
            .ToDictionaryAsync(x => x.Id, x => x.ParentCourseId, ct);

        var seen = new HashSet<Guid>();
        var current = parentId.Value;
        while (true)
        {
            if (current == courseId) return true;
            if (!seen.Add(current)) return true;
            if (!parents.TryGetValue(current, out var next) || !next.HasValue) return false;
            current = next.Value;
        }
    }
}
