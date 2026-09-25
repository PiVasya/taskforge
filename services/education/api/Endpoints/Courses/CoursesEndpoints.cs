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
            var unavailableCourseIds = BuildUnavailableCourseIds(access, rows);
            var visibleRows = rows.Where(x => !unavailableCourseIds.Contains(x.Id));
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

        app.MapPost("/api/courses", async (CourseRequest request, HttpContext http, IConfiguration cfg, EducationDbContext db, IHttpClientFactory clients, CancellationToken ct) =>
        {
            var access = await ResolveAccessContext(http, cfg, db, ct);
            if (!access.UserId.HasValue) return Microsoft.AspNetCore.Http.Results.Unauthorized();
            if (!access.IsEditorOrAdmin) return CourseEditForbidden();

            var currentUserId = access.UserId;
            var ownerIds = request.OwnerIds?.Where(x => x != Guid.Empty).Distinct().ToArray();
            if ((ownerIds == null || ownerIds.Length == 0) && currentUserId.HasValue)
            {
                ownerIds = new[] { currentUserId.Value };
            }
            if (ownerIds is { Length: > 0 })
            {
                var requestedLevels = await LoadUserAccessLevelsAsync(ownerIds.Append(currentUserId!.Value), clients, cfg, ct);
                var mergedRanks = new Dictionary<Guid, int>(access.UserRanks ?? new Dictionary<Guid, int>());
                foreach (var level in requestedLevels) mergedRanks[level.UserId] = level.EffectiveRank;
                access = access with { UserRanks = mergedRanks };
                if (ownerIds.Any(ownerId => !CanAssignCourseOwner(access, ownerId)))
                    return CourseOwnerForbidden();
            }

            var parentId = request.ParentCourseId == Guid.Empty ? null : request.ParentCourseId;
            if (parentId.HasValue)
            {
                var parent = await db.Courses.AsNoTracking().FirstOrDefaultAsync(x => x.Id == parentId.Value, ct);
                if (parent is null)
                {
                    return Microsoft.AspNetCore.Http.Results.Json(new { message = "Родительский курс не найден.", code = "COURSE_PARENT_NOT_FOUND" }, statusCode: StatusCodes.Status400BadRequest);
                }
                if (!CanEditCourse(access, parent)) return CourseEditForbidden();
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
            NormalizeCourseAudience(course);
            db.Courses.Add(course);
            await db.SaveChangesAsync(ct);
            return Microsoft.AspNetCore.Http.Results.Ok(ToCourseDto(course, CanEditCourse(access, course)));
        });

        app.MapGet("/api/courses/{id:guid}", async (Guid id, HttpContext http, EducationDbContext db, IConfiguration cfg, CancellationToken ct) =>
        {
            var access = await ResolveAccessContext(http, cfg, db, ct);
            if (!access.UserId.HasValue) return Microsoft.AspNetCore.Http.Results.Unauthorized();

            var rows = await db.Courses.AsNoTracking().ToListAsync(ct);
            var course = rows.FirstOrDefault(x => x.Id == id);
            var unavailableCourseIds = BuildUnavailableCourseIds(access, rows);
            if (course == null || unavailableCourseIds.Contains(course.Id)) return Microsoft.AspNetCore.Http.Results.NotFound();

            return Microsoft.AspNetCore.Http.Results.Ok(ToCourseDto(course, CanEditCourse(access, course)));
        });

        app.MapPut("/api/courses/{id:guid}", async (Guid id, CourseRequest request, HttpContext http, IConfiguration cfg, EducationDbContext db, CancellationToken ct) =>
        {
            var access = await ResolveAccessContext(http, cfg, db, ct);
            if (!access.UserId.HasValue) return Microsoft.AspNetCore.Http.Results.Unauthorized();

            var course = await db.Courses.FindAsync(new object[] { id }, ct);
            if (course == null) return Microsoft.AspNetCore.Http.Results.NotFound();
            if (!CanEditCourse(access, course)) return CourseEditForbidden();
            if (!string.IsNullOrWhiteSpace(request.Title)) course.Title = request.Title.Trim();
            course.Description = request.Description;
            if (request.IsPublic.HasValue) course.IsPublic = request.IsPublic.Value;
            if (request.IsHiddenFromStudents.HasValue) course.IsHiddenFromStudents = request.IsHiddenFromStudents.Value;
            if (request.Sort.HasValue) course.Sort = System.Math.Max(0, request.Sort.Value);
            if (request.OwnerIds != null) course.OwnerIdsJson = Serialize(request.OwnerIds);
            if (request.VisibleGroupIds != null) course.VisibleGroupIdsJson = Serialize(request.VisibleGroupIds);
            NormalizeCourseAudience(course);
            course.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            return Microsoft.AspNetCore.Http.Results.Ok(ToCourseDto(course, CanEditCourse(access, course)));
        });

        app.MapDelete("/api/courses/{id:guid}", async (Guid id, HttpContext http, IConfiguration cfg, EducationDbContext db, CancellationToken ct) =>
        {
            var access = await ResolveAccessContext(http, cfg, db, ct);
            if (!access.UserId.HasValue) return Microsoft.AspNetCore.Http.Results.Unauthorized();

            var rows = await db.Courses
                .Select(x => new { x.Id, x.ParentCourseId })
                .ToListAsync(ct);
            if (rows.All(x => x.Id != id)) return Microsoft.AspNetCore.Http.Results.NotFound();

            var subtreeIds = CollectCourseSubtreeIds(id, rows.Select(x => (x.Id, x.ParentCourseId)));
            var subtree = await db.Courses
                .Where(x => subtreeIds.Contains(x.Id))
                .ToListAsync(ct);
            if (subtree.Any(course => !CanEditCourse(access, course))) return CourseEditForbidden();

            db.Courses.RemoveRange(subtree);
            await db.SaveChangesAsync(ct);
            return Microsoft.AspNetCore.Http.Results.Ok(new { message = "deleted", deletedCourses = subtree.Count });
        });

        app.MapPatch("/api/courses/{courseId:guid}/sort", async (Guid courseId, CourseSortRequest request, HttpContext http, IConfiguration cfg, EducationDbContext db, CancellationToken ct) =>
        {
            var access = await ResolveAccessContext(http, cfg, db, ct);
            if (!access.UserId.HasValue) return Microsoft.AspNetCore.Http.Results.Unauthorized();

            var course = await db.Courses.FindAsync(new object[] { courseId }, ct);
            if (course == null) return Microsoft.AspNetCore.Http.Results.NotFound(new { message = "Курс не найден.", code = "COURSE_NOT_FOUND" });
            if (!CanEditCourse(access, course)) return CourseEditForbidden();
            course.Sort = System.Math.Max(0, request.Sort);
            course.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            return Microsoft.AspNetCore.Http.Results.Ok(ToCourseDto(course, CanEditCourse(access, course)));
        });

        app.MapPatch("/api/courses/{courseId:guid}/position", async (Guid courseId, CoursePositionRequest request, HttpContext http, IConfiguration cfg, EducationDbContext db, CancellationToken ct) =>
        {
            var access = await ResolveAccessContext(http, cfg, db, ct);
            if (!access.UserId.HasValue) return Microsoft.AspNetCore.Http.Results.Unauthorized();

            var course = await db.Courses.FindAsync(new object[] { courseId }, ct);
            if (course == null) return Microsoft.AspNetCore.Http.Results.NotFound(new { message = "Курс не найден.", code = "COURSE_NOT_FOUND" });
            if (!CanEditCourse(access, course)) return CourseEditForbidden();

            var parentId = request.ParentCourseId == Guid.Empty ? null : request.ParentCourseId;
            if (parentId == course.Id)
            {
                return Microsoft.AspNetCore.Http.Results.Json(new { message = "Курс нельзя вложить сам в себя.", code = "COURSE_PARENT_SELF" }, statusCode: StatusCodes.Status400BadRequest);
            }
            if (parentId.HasValue)
            {
                var parent = await db.Courses.AsNoTracking().FirstOrDefaultAsync(x => x.Id == parentId.Value, ct);
                if (parent is null)
                {
                    return Microsoft.AspNetCore.Http.Results.Json(new { message = "Родительский курс не найден.", code = "COURSE_PARENT_NOT_FOUND" }, statusCode: StatusCodes.Status400BadRequest);
                }
                if (!CanEditCourse(access, parent)) return CourseEditForbidden();
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
            if (siblings.Any(sibling => !CanEditCourse(access, sibling))) return CourseEditForbidden();

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
            return Microsoft.AspNetCore.Http.Results.Ok(ToCourseDto(course, CanEditCourse(access, course)));
        });

        app.MapPost("/api/courses/{courseId:guid}/visible-groups", async (Guid courseId, CourseGroupsRequest request, HttpContext http, IConfiguration cfg, EducationDbContext db, CancellationToken ct) =>
        {
            var access = await ResolveAccessContext(http, cfg, db, ct);
            if (!access.UserId.HasValue) return Microsoft.AspNetCore.Http.Results.Unauthorized();

            var course = await db.Courses.FindAsync(new object[] { courseId }, ct);
            if (course == null) return Microsoft.AspNetCore.Http.Results.NotFound();
            if (!CanEditCourse(access, course)) return CourseEditForbidden();
            course.VisibleGroupIdsJson = Serialize(request.GroupIds);
            course.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            return Microsoft.AspNetCore.Http.Results.Ok(ToCourseDto(course, CanEditCourse(access, course)));
        });

        app.MapPost("/api/courses/{courseId:guid}/owners", async (Guid courseId, CourseOwnersRequest request, HttpContext http, IConfiguration cfg, EducationDbContext db, IHttpClientFactory clients, CancellationToken ct) =>
        {
            var access = await ResolveAccessContext(http, cfg, db, ct);
            if (!access.UserId.HasValue) return Microsoft.AspNetCore.Http.Results.Unauthorized();

            var course = await db.Courses.FindAsync(new object[] { courseId }, ct);
            if (course == null) return Microsoft.AspNetCore.Http.Results.NotFound();
            if (!CanEditCourse(access, course)) return CourseEditForbidden();
            var requestedOwnerIds = (request.OwnerIds ?? Array.Empty<Guid>()).Where(x => x != Guid.Empty).Distinct().ToArray();
            var currentOwnerIds = DeserializeIds(course.OwnerIdsJson);
            var ownerIdsToValidate = currentOwnerIds.Concat(requestedOwnerIds).Append(access.UserId!.Value).Distinct().ToArray();
            var requestedLevels = await LoadUserAccessLevelsAsync(ownerIdsToValidate, clients, cfg, ct);
            var mergedRanks = new Dictionary<Guid, int>(access.UserRanks ?? new Dictionary<Guid, int>());
            foreach (var level in requestedLevels) mergedRanks[level.UserId] = level.EffectiveRank;
            access = access with { UserRanks = mergedRanks };
            if (!access.IsSuperAdmin && currentOwnerIds.Where(ownerId => ownerId != access.UserId.Value).Any(ownerId => !CanAssignCourseOwner(access, ownerId))) return CourseOwnerForbidden();
            if (requestedOwnerIds.Any(ownerId => !CanAssignCourseOwner(access, ownerId))) return CourseOwnerForbidden();
            course.OwnerIdsJson = Serialize(requestedOwnerIds);
            course.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            return Microsoft.AspNetCore.Http.Results.Ok(ToCourseDto(course, CanEditCourse(access, course)));
        });

        return app;
    }


    private static IResult CourseEditForbidden()
        => Microsoft.AspNetCore.Http.Results.Json(
            new { message = "Недостаточно прав для изменения курса.", code = "COURSE_EDIT_FORBIDDEN" },
            statusCode: StatusCodes.Status403Forbidden);

    private static IResult CourseOwnerForbidden()
        => Microsoft.AspNetCore.Http.Results.Json(
            new { message = "Нельзя назначить владельца курса с равной или более высокой ролью.", code = "COURSE_OWNER_FORBIDDEN" },
            statusCode: StatusCodes.Status403Forbidden);

    private static HashSet<Guid> CollectCourseSubtreeIds(Guid rootCourseId, IEnumerable<(Guid Id, Guid? ParentCourseId)> rows)
    {
        var childrenByParent = rows
            .Where(x => x.ParentCourseId.HasValue)
            .GroupBy(x => x.ParentCourseId!.Value)
            .ToDictionary(x => x.Key, x => x.Select(row => row.Id).ToArray());

        var result = new HashSet<Guid> { rootCourseId };
        var queue = new Queue<Guid>();
        queue.Enqueue(rootCourseId);

        while (queue.Count > 0)
        {
            var parentId = queue.Dequeue();
            if (!childrenByParent.TryGetValue(parentId, out var children)) continue;
            foreach (var childId in children)
            {
                if (result.Add(childId)) queue.Enqueue(childId);
            }
        }

        return result;
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
