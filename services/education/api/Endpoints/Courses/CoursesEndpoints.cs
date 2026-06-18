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
        app.MapGet("/api/courses", async (HttpContext http, EducationDbContext db, IConfiguration cfg, int? page, int? pageSize, string? q, CancellationToken ct) =>
        {
            var access = await ResolveAccessContext(http, cfg, db, ct);
            if (!access.UserId.HasValue) return Results.Unauthorized();

            var normalizedQuery = string.Join(' ', (q ?? string.Empty).Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            var requestedPagedShape = page.HasValue || pageSize.HasValue || !string.IsNullOrWhiteSpace(normalizedQuery);
            var size = Math.Clamp(pageSize ?? 12, 1, 50);
            var currentPage = Math.Max(1, page ?? 1);

            var rows = await db.Courses.AsNoTracking().OrderBy(x => x.Title).ToListAsync(ct);
            var visibleRows = rows.Where(x => CanViewCourse(access, x));
            if (!string.IsNullOrWhiteSpace(normalizedQuery))
            {
                visibleRows = visibleRows.Where(x =>
                    x.Title.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase)
                    || (!string.IsNullOrWhiteSpace(x.Description) && x.Description.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase)));
            }

            var visible = visibleRows.ToList();
            if (!requestedPagedShape)
            {
                return Results.Ok(visible.Select(x => ToCourseDto(x, CanEditCourse(access, x))).ToList());
            }

            var total = visible.Count;
            var pageRows = visible.Skip((currentPage - 1) * size).Take(size).Select(x => ToCourseDto(x, CanEditCourse(access, x))).ToList();
            return Results.Ok(new PagedResult<CourseDto>(pageRows, currentPage, size, total, currentPage * size < total));
        });

        app.MapPost("/api/courses", async (CourseRequest request, HttpContext http, IConfiguration cfg, EducationDbContext db) =>
        {
            var currentUserId = TaskForgeRequestSecurity.UserId(http, cfg);
            var ownerIds = request.OwnerIds?.Where(x => x != Guid.Empty).Distinct().ToArray();
            if ((ownerIds == null || ownerIds.Length == 0) && currentUserId.HasValue)
            {
                ownerIds = new[] { currentUserId.Value };
            }

            var course = new Course
            {
                Title = string.IsNullOrWhiteSpace(request.Title) ? "Новый курс" : request.Title.Trim(),
                Description = request.Description,
                IsPublic = request.IsPublic ?? false,
                OwnerIdsJson = Serialize(ownerIds),
                VisibleGroupIdsJson = Serialize(request.VisibleGroupIds)
            };
            db.Courses.Add(course);
            await db.SaveChangesAsync();
            return Results.Ok(ToCourseDto(course, canEdit: true));
        });

        app.MapGet("/api/courses/{id:guid}", async (Guid id, HttpContext http, EducationDbContext db, IConfiguration cfg, CancellationToken ct) =>
        {
            var access = await ResolveAccessContext(http, cfg, db, ct);
            if (!access.UserId.HasValue) return Results.Unauthorized();

            var course = await db.Courses.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
            if (course == null || !CanViewCourse(access, course)) return Results.NotFound();

            return Results.Ok(ToCourseDto(course, CanEditCourse(access, course)));
        });

        app.MapPut("/api/courses/{id:guid}", async (Guid id, CourseRequest request, EducationDbContext db) =>
        {
            var course = await db.Courses.FindAsync(id);
            if (course == null) return Results.NotFound();
            if (!string.IsNullOrWhiteSpace(request.Title)) course.Title = request.Title.Trim();
            course.Description = request.Description;
            if (request.IsPublic.HasValue) course.IsPublic = request.IsPublic.Value;
            if (request.OwnerIds != null) course.OwnerIdsJson = Serialize(request.OwnerIds);
            if (request.VisibleGroupIds != null) course.VisibleGroupIdsJson = Serialize(request.VisibleGroupIds);
            course.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
            return Results.Ok(ToCourseDto(course, canEdit: true));
        });

        app.MapDelete("/api/courses/{id:guid}", async (Guid id, EducationDbContext db) =>
        {
            var course = await db.Courses.FindAsync(id);
            if (course == null) return Results.NotFound();
            db.Courses.Remove(course);
            await db.SaveChangesAsync();
            return Results.Ok(new { message = "deleted" });
        });

        app.MapPost("/api/courses/{courseId:guid}/visible-groups", async (Guid courseId, CourseGroupsRequest request, EducationDbContext db) =>
        {
            var course = await db.Courses.FindAsync(courseId);
            if (course == null) return Results.NotFound();
            course.VisibleGroupIdsJson = Serialize(request.GroupIds);
            course.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
            return Results.Ok(ToCourseDto(course, canEdit: true));
        });

        app.MapPost("/api/courses/{courseId:guid}/owners", async (Guid courseId, CourseOwnersRequest request, EducationDbContext db) =>
        {
            var course = await db.Courses.FindAsync(courseId);
            if (course == null) return Results.NotFound();
            course.OwnerIdsJson = Serialize(request.OwnerIds);
            course.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
            return Results.Ok(ToCourseDto(course, canEdit: true));
        });

        return app;
    }
}
