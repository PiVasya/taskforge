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
    private static WebApplication MapInternalEndpoints(WebApplication app)
    {
        app.MapGet("/api/admin/users/{userId:guid}/groups", async (Guid userId, EducationDbContext db) => Microsoft.AspNetCore.Http.Results.Ok(await db.GroupMembers.AsNoTracking().Where(x => x.UserId == userId).Select(x => x.GroupId).ToListAsync()));

        app.MapPost("/api/internal/courses/metadata", async (CourseIdsRequest request, EducationDbContext db, IDistributedCache cache, IConfiguration cfg, ILogger<Program> logger, CancellationToken ct) =>
        {
            var ids = (request.CourseIds ?? Array.Empty<Guid>()).Where(x => x != Guid.Empty).Distinct().Take(1000).OrderBy(x => x).ToArray();
            if (ids.Length == 0) return Microsoft.AspNetCore.Http.Results.Ok(Array.Empty<CourseMetadataDto>());
            var key = TaskForgeCache.Key("education:course-metadata:v2", ids);
            var rows = await TaskForgeCache.GetOrSetAsync(cache, cfg, logger, key, TaskForgeCache.Ttl(cfg, "Metadata", 300), async token =>
            {
                var courses = await db.Courses.AsNoTracking().Where(x => ids.Contains(x.Id)).ToListAsync(token);
                return courses.Select(x => new CourseMetadataDto(x.Id, x.Id, x.Title, x.Title, x.Description, x.IsPublic && !x.IsHiddenFromStudents)).ToList();
            }, ct);
            return Microsoft.AspNetCore.Http.Results.Ok(rows);
        });

        app.MapGet("/api/internal/groups/{groupId:guid}/members", async (Guid groupId, EducationDbContext db, CancellationToken ct) =>
        {
            var ids = await db.GroupMembers.AsNoTracking().Where(x => x.GroupId == groupId).Select(x => x.UserId).ToListAsync(ct);
            return Microsoft.AspNetCore.Http.Results.Ok(new { groupId, userIds = ids });
        });

        app.MapGet("/api/internal/users/{userId:guid}/groups", async (Guid userId, EducationDbContext db, CancellationToken ct) =>
        {
            var ids = await db.GroupMembers.AsNoTracking().Where(x => x.UserId == userId).Select(x => x.GroupId).ToListAsync(ct);
            return Microsoft.AspNetCore.Http.Results.Ok(new { userId, groupIds = ids });
        });

        app.MapGet("/api/internal/courses/{courseId:guid}/access/{userId:guid}", async (Guid courseId, Guid userId, EducationDbContext db, CancellationToken ct) =>
        {
            TaskForgeDebugTrace.Map("EDU_ACCESS_BEGIN", ("user", userId), ("course", courseId), ("mode", "single"));
            var allById = await LoadCoursesWithAncestorsAsync(new[] { courseId }, db, ct);
            if (!allById.TryGetValue(courseId, out var course)) return Microsoft.AspNetCore.Http.Results.NotFound();

            var groupIds = await db.GroupMembers.AsNoTracking().Where(x => x.UserId == userId).Select(x => x.GroupId).ToListAsync(ct);
            var access = new EducationAccessContext(userId, false, groupIds.ToHashSet());
            var rootCourseId = ResolveRootCourseId(course, allById);
            var mapJson = await db.CourseMaps.AsNoTracking()
                .Where(x => x.RootCourseId == rootCourseId)
                .Select(x => x.DocumentJson)
                .FirstOrDefaultAsync(ct);

            var canView = CanViewCourseWithAncestors(access, course, allById);
            var canEdit = CanEditCourse(access, course);
            var hasProgressionRules = ContainsProgressionRules(mapJson);
            TaskForgeDebugTrace.Map("EDU_ACCESS_END",
                ("user", userId),
                ("course", course.Id),
                ("rootCourse", rootCourseId),
                ("canView", canView),
                ("canEdit", canEdit),
                ("publicToStudents", course.IsPublic && !course.IsHiddenFromStudents),
                ("hasProgressionRules", hasProgressionRules),
                ("groupIds", TaskForgeDebugTrace.MapList(groupIds)),
                ("mode", "single"));
            return Microsoft.AspNetCore.Http.Results.Ok(new CourseAccessDto(
                course.Id,
                userId,
                canView,
                canEdit,
                course.IsPublic && !course.IsHiddenFromStudents,
                rootCourseId,
                hasProgressionRules));
        });


        app.MapPost("/api/internal/courses/access", async (CourseAccessBatchRequest request, EducationDbContext db, CancellationToken ct) =>
        {
            var courseIds = (request.CourseIds ?? Array.Empty<Guid>())
                .Where(x => x != Guid.Empty)
                .Distinct()
                .Take(2000)
                .ToArray();
            TaskForgeDebugTrace.Map("EDU_ACCESS_BATCH_BEGIN",
                ("user", request.UserId),
                ("courseCount", courseIds.Length),
                ("courseIds", TaskForgeDebugTrace.MapList(courseIds)),
                ("bypassStudentVisibility", request.BypassStudentVisibility),
                ("includeProgressionRules", request.IncludeProgressionRules));

            if (request.UserId == Guid.Empty || courseIds.Length == 0)
            {
                return Microsoft.AspNetCore.Http.Results.Ok(Array.Empty<CourseAccessDto>());
            }

            var groupIds = await db.GroupMembers.AsNoTracking()
                .Where(x => x.UserId == request.UserId)
                .Select(x => x.GroupId)
                .ToListAsync(ct);
            var access = new EducationAccessContext(request.UserId, request.BypassStudentVisibility, groupIds.ToHashSet());
            var allById = await LoadCoursesWithAncestorsAsync(courseIds, db, ct);
            var courses = courseIds.Where(allById.ContainsKey).Select(id => allById[id]).ToList();

            var byId = courses.ToDictionary(x => x.Id);
            var rootByCourseId = courses.ToDictionary(x => x.Id, x => ResolveRootCourseId(x, allById));
            var progressionByRootId = new Dictionary<Guid, bool>();
            if (request.IncludeProgressionRules)
            {
                var rootIds = rootByCourseId.Values.Distinct().ToArray();
                var mapRows = await db.CourseMaps.AsNoTracking()
                    .Where(x => rootIds.Contains(x.RootCourseId))
                    .Select(x => new { x.RootCourseId, x.DocumentJson })
                    .ToListAsync(ct);
                progressionByRootId = mapRows.ToDictionary(x => x.RootCourseId, x => ContainsProgressionRules(x.DocumentJson));
            }

            var rows = courseIds
                .Where(byId.ContainsKey)
                .Select(id =>
                {
                    var course = byId[id];
                    var rootCourseId = rootByCourseId[id];
                    return new CourseAccessDto(
                        course.Id,
                        request.UserId,
                        CanViewCourseWithAncestors(access, course, allById),
                        CanEditCourse(access, course),
                        course.IsPublic && !course.IsHiddenFromStudents,
                        rootCourseId,
                        progressionByRootId.GetValueOrDefault(rootCourseId));
                })
                .ToArray();

            TaskForgeDebugTrace.Map("EDU_ACCESS_BATCH_END",
                ("user", request.UserId),
                ("rowCount", rows.Length),
                ("visibleCourseIds", TaskForgeDebugTrace.MapList(rows.Where(x => x.CanView).Select(x => x.CourseId))),
                ("deniedCourseIds", TaskForgeDebugTrace.MapList(rows.Where(x => !x.CanView).Select(x => x.CourseId))),
                ("rootCourseIds", TaskForgeDebugTrace.MapList(rows.Select(x => x.RootCourseId))),
                ("groupIds", TaskForgeDebugTrace.MapList(groupIds)),
                ("bypassStudentVisibility", request.BypassStudentVisibility));
            return Microsoft.AspNetCore.Http.Results.Ok(rows);
        });

        app.MapPost("/api/internal/courses/import-ensure", async (CourseGraphImportEnsureRequest request, EducationDbContext db, CancellationToken ct) =>
        {
            if (request.RootCourseId == Guid.Empty)
                return Microsoft.AspNetCore.Http.Results.BadRequest(new { message = "Не указан корневой курс.", code = "COURSE_IMPORT_ROOT_REQUIRED" });

            var root = await db.Courses.AsNoTracking().FirstOrDefaultAsync(x => x.Id == request.RootCourseId, ct);
            if (root == null)
                return Microsoft.AspNetCore.Http.Results.NotFound(new { message = "Корневой курс не найден.", code = "COURSE_IMPORT_ROOT_NOT_FOUND" });

            var items = (request.Courses ?? Array.Empty<CourseGraphImportItemRequest>())
                .Where(x => !string.IsNullOrWhiteSpace(x.Key))
                .Take(1000)
                .ToArray();
            if (items.Length == 0)
                return Microsoft.AspNetCore.Http.Results.Ok(new CourseGraphImportEnsureResponse(new List<CourseGraphImportItemResponse>()));

            if (items.Select(x => x.Key.Trim()).Distinct(StringComparer.Ordinal).Count() != items.Length)
                return Microsoft.AspNetCore.Http.Results.BadRequest(new { message = "Ключи вложенных курсов должны быть уникальны.", code = "COURSE_IMPORT_DUPLICATE_KEY" });

            var requestedIds = items.Where(x => x.Id.HasValue && x.Id.Value != Guid.Empty).Select(x => x.Id!.Value).ToArray();
            if (requestedIds.Distinct().Count() != requestedIds.Length)
                return Microsoft.AspNetCore.Http.Results.BadRequest(new { message = "UUID вложенных курсов должны быть уникальны.", code = "COURSE_IMPORT_DUPLICATE_ID" });
            if (requestedIds.Contains(request.RootCourseId))
                return Microsoft.AspNetCore.Http.Results.BadRequest(new { message = "Нельзя объявить текущий корневой курс как вложенный.", code = "COURSE_IMPORT_ROOT_AS_CHILD" });

            var subtreeRows = await LoadCourseSubtreeRowsAsync(request.RootCourseId, db, ct);
            var subtreeIds = subtreeRows.Select(x => x.Id).ToHashSet();
            var existingRequested = requestedIds.Length == 0
                ? new List<Course>()
                : await db.Courses.Where(x => requestedIds.Contains(x.Id)).ToListAsync(ct);
            var foreign = existingRequested.Where(x => !subtreeIds.Contains(x.Id)).Select(x => x.Id).ToArray();
            if (foreign.Length > 0)
            {
                return Microsoft.AspNetCore.Http.Results.Conflict(new
                {
                    message = "Один из UUID принадлежит курсу вне импортируемого поддерева.",
                    code = "COURSE_IMPORT_FOREIGN_ID",
                    courseIds = foreign
                });
            }

            var existingById = existingRequested.ToDictionary(x => x.Id);
            var maxSort = await db.Courses.Where(x => x.ParentCourseId == request.RootCourseId).Select(x => (int?)x.Sort).MaxAsync(ct) ?? -1;
            var created = new List<Course>();
            var response = new List<CourseGraphImportItemResponse>();

            foreach (var item in items)
            {
                var key = item.Key.Trim();
                var desiredId = item.Id.HasValue && item.Id.Value != Guid.Empty ? item.Id.Value : Guid.NewGuid();
                if (existingById.TryGetValue(desiredId, out var existing))
                {
                    response.Add(new CourseGraphImportItemResponse(key, existing.Id, existing.Title, false));
                    continue;
                }

                var title = string.IsNullOrWhiteSpace(item.Title) ? "Новый вложенный курс" : item.Title.Trim();
                var course = new Course
                {
                    Id = desiredId,
                    Title = title,
                    Description = null,
                    IsPublic = false,
                    IsHiddenFromStudents = false,
                    ParentCourseId = request.RootCourseId,
                    Sort = ++maxSort,
                    OwnerIdsJson = Serialize(request.OwnerId.HasValue && request.OwnerId.Value != Guid.Empty ? new[] { request.OwnerId.Value } : Array.Empty<Guid>()),
                    VisibleGroupIdsJson = "[]"
                };
                NormalizeCourseAudience(course);
                created.Add(course);
                existingById[course.Id] = course;
                response.Add(new CourseGraphImportItemResponse(key, course.Id, course.Title, true));
            }

            if (created.Count > 0)
            {
                db.Courses.AddRange(created);
                await db.SaveChangesAsync(ct);
            }

            return Microsoft.AspNetCore.Http.Results.Ok(new CourseGraphImportEnsureResponse(response));
        });

        app.MapGet("/api/internal/courses/{courseId:guid}/tree", async (Guid courseId, EducationDbContext db, CancellationToken ct) =>
        {
            TaskForgeDebugTrace.Map("EDU_TREE_BEGIN", ("course", courseId));
            var courses = await LoadCourseSubtreeRowsAsync(courseId, db, ct);
            if (courses.Count == 0)
            {
                TaskForgeDebugTrace.Map("EDU_TREE_END", ("course", courseId), ("found", false));
                return Microsoft.AspNetCore.Http.Results.NotFound();
            }
            TaskForgeDebugTrace.Map("EDU_TREE_END",
                ("course", courseId),
                ("found", true),
                ("courseCount", courses.Count),
                ("courseIds", TaskForgeDebugTrace.MapList(courses.Select(x => x.Id))));
            return Microsoft.AspNetCore.Http.Results.Ok(new CourseTreeResponse(courseId, courses.Select(x => x.Id).ToArray(), courses));
        });

        app.MapGet("/api/internal/courses/{courseId:guid}/map/meta", async (Guid courseId, EducationDbContext db, CancellationToken ct) =>
        {
            TaskForgeDebugTrace.Map("EDU_MAP_META_BEGIN", ("requestedCourse", courseId));
            var root = await ResolveRootCourseAsync(courseId, db, ct);
            if (root is null)
            {
                TaskForgeDebugTrace.Map("EDU_MAP_META_END", ("requestedCourse", courseId), ("found", false), ("reason", "root-missing"));
                return Microsoft.AspNetCore.Http.Results.NotFound();
            }
            var map = await db.CourseMaps.AsNoTracking()
                .Where(x => x.RootCourseId == root.Id)
                .Select(x => new { x.Version, x.UpdatedAt, x.UpdatedBy })
                .FirstOrDefaultAsync(ct);
            TaskForgeDebugTrace.Map("EDU_MAP_META_END",
                ("requestedCourse", courseId),
                ("rootCourse", root.Id),
                ("found", true),
                ("hasStoredMap", map is not null),
                ("version", map?.Version ?? 0),
                ("updatedAt", map?.UpdatedAt),
                ("updatedBy", map?.UpdatedBy));
            return Microsoft.AspNetCore.Http.Results.Ok(new CourseMapMetaResponse(
                root.Id,
                courseId,
                map?.Version ?? 0,
                map?.UpdatedAt,
                map?.UpdatedBy));
        });

        app.MapGet("/api/internal/courses/{courseId:guid}/map", async (Guid courseId, EducationDbContext db, CancellationToken ct) =>
        {
            TaskForgeDebugTrace.Map("EDU_MAP_BEGIN", ("requestedCourse", courseId));
            var root = await ResolveRootCourseAsync(courseId, db, ct);
            if (root is null)
            {
                TaskForgeDebugTrace.Map("EDU_MAP_END", ("requestedCourse", courseId), ("found", false), ("reason", "root-missing"));
                return Microsoft.AspNetCore.Http.Results.NotFound();
            }
            var map = await db.CourseMaps.AsNoTracking().FirstOrDefaultAsync(x => x.RootCourseId == root.Id, ct);
            var document = map is null ? (JsonElement?)null : ParseDocumentElement(map.DocumentJson);
            var nodeCount = document.HasValue && document.Value.ValueKind == JsonValueKind.Object && document.Value.TryGetProperty("nodes", out var nodes) && nodes.ValueKind == JsonValueKind.Array ? nodes.GetArrayLength() : 0;
            var edgeCount = document.HasValue && document.Value.ValueKind == JsonValueKind.Object && document.Value.TryGetProperty("edges", out var edges) && edges.ValueKind == JsonValueKind.Array ? edges.GetArrayLength() : 0;
            TaskForgeDebugTrace.Map("EDU_MAP_END",
                ("requestedCourse", courseId),
                ("rootCourse", root.Id),
                ("found", true),
                ("hasStoredMap", map is not null),
                ("version", map?.Version ?? 0),
                ("documentHash", TaskForgeDebugTrace.Fingerprint(map?.DocumentJson)),
                ("nodeCount", nodeCount),
                ("edgeCount", edgeCount),
                ("updatedAt", map?.UpdatedAt),
                ("updatedBy", map?.UpdatedBy));
            return Microsoft.AspNetCore.Http.Results.Ok(new CourseMapResponse(
                root.Id,
                courseId,
                map?.Version ?? 0,
                document,
                map?.UpdatedAt,
                map?.UpdatedBy));
        });

        return app;
    }

    private static async Task<Dictionary<Guid, Course>> LoadCoursesWithAncestorsAsync(
        IEnumerable<Guid> courseIds,
        EducationDbContext db,
        CancellationToken ct)
    {
        var result = new Dictionary<Guid, Course>();
        var frontier = courseIds.Where(x => x != Guid.Empty).Distinct().ToArray();

        while (frontier.Length > 0)
        {
            var rows = await db.Courses.AsNoTracking().Where(x => frontier.Contains(x.Id)).ToListAsync(ct);
            foreach (var row in rows) result[row.Id] = row;

            frontier = rows
                .Where(x => x.ParentCourseId.HasValue && !result.ContainsKey(x.ParentCourseId.Value))
                .Select(x => x.ParentCourseId!.Value)
                .Distinct()
                .ToArray();
        }

        return result;
    }

    private static Guid ResolveRootCourseId(Course course, IReadOnlyDictionary<Guid, Course> byId)
    {
        var current = course;
        var seen = new HashSet<Guid>();
        while (current.ParentCourseId.HasValue && seen.Add(current.Id) && byId.TryGetValue(current.ParentCourseId.Value, out var parent))
        {
            current = parent;
        }
        return current.Id;
    }

    private static bool ContainsProgressionRules(string? documentJson)
    {
        if (string.IsNullOrWhiteSpace(documentJson)) return false;
        try
        {
            using var doc = JsonDocument.Parse(documentJson);
            if (!doc.RootElement.TryGetProperty("edges", out var edges) || edges.ValueKind != JsonValueKind.Array) return false;
            foreach (var edge in edges.EnumerateArray())
            {
                if (edge.ValueKind != JsonValueKind.Object
                    || !edge.TryGetProperty("settings", out var settings)
                    || settings.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (settings.TryGetProperty("gateUntilPrerequisites", out var gate)
                    && gate.ValueKind == JsonValueKind.True)
                {
                    return true;
                }
                if (settings.TryGetProperty("sequentialReveal", out var sequential)
                    && sequential.ValueKind == JsonValueKind.True)
                {
                    return true;
                }
                foreach (var effectName in new[] { "hiddenEffect", "sequentialEffect" })
                {
                    if (!settings.TryGetProperty(effectName, out var effect) || effect.ValueKind != JsonValueKind.String) continue;
                    var value = effect.GetString()?.Trim();
                    if (string.Equals(value, "start", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(value, "stop", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                if (settings.TryGetProperty("accessMode", out var mode) && mode.ValueKind == JsonValueKind.String)
                {
                    var value = mode.GetString()?.Trim();
                    if (string.Equals(value, "after-prerequisites", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(value, "sequential", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
        }
        catch (JsonException)
        {
            // Course-map writes validate JSON. Treat legacy/corrupt documents as
            // having no progression flags here; the map endpoint remains the source
            // of truth and will not expose invalid progression settings to learners.
        }
        return false;
    }
}
