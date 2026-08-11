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
            var allById = await LoadCoursesWithAncestorsAsync(new[] { courseId }, db, ct);
            if (!allById.TryGetValue(courseId, out var course)) return Microsoft.AspNetCore.Http.Results.NotFound();

            var groupIds = await db.GroupMembers.AsNoTracking().Where(x => x.UserId == userId).Select(x => x.GroupId).ToListAsync(ct);
            var access = new EducationAccessContext(userId, false, groupIds.ToHashSet());
            var rootCourseId = ResolveRootCourseId(course, allById);
            var mapJson = await db.CourseMaps.AsNoTracking()
                .Where(x => x.RootCourseId == rootCourseId)
                .Select(x => x.DocumentJson)
                .FirstOrDefaultAsync(ct);

            return Microsoft.AspNetCore.Http.Results.Ok(new CourseAccessDto(
                course.Id,
                userId,
                CanViewCourseWithAncestors(access, course, allById),
                CanEditCourse(access, course),
                course.IsPublic && !course.IsHiddenFromStudents,
                rootCourseId,
                ContainsProgressionRules(mapJson)));
        });


        app.MapPost("/api/internal/courses/access", async (CourseAccessBatchRequest request, EducationDbContext db, CancellationToken ct) =>
        {
            var courseIds = (request.CourseIds ?? Array.Empty<Guid>())
                .Where(x => x != Guid.Empty)
                .Distinct()
                .Take(2000)
                .ToArray();

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

            return Microsoft.AspNetCore.Http.Results.Ok(rows);
        });

        app.MapGet("/api/internal/courses/{courseId:guid}/tree", async (Guid courseId, EducationDbContext db, CancellationToken ct) =>
        {
            var courses = await LoadCourseSubtreeRowsAsync(courseId, db, ct);
            if (courses.Count == 0) return Microsoft.AspNetCore.Http.Results.NotFound();
            return Microsoft.AspNetCore.Http.Results.Ok(new CourseTreeResponse(courseId, courses.Select(x => x.Id).ToArray(), courses));
        });

        app.MapGet("/api/internal/courses/{courseId:guid}/map/meta", async (Guid courseId, EducationDbContext db, CancellationToken ct) =>
        {
            var root = await ResolveRootCourseAsync(courseId, db, ct);
            if (root is null) return Microsoft.AspNetCore.Http.Results.NotFound();
            var map = await db.CourseMaps.AsNoTracking()
                .Where(x => x.RootCourseId == root.Id)
                .Select(x => new { x.Version, x.UpdatedAt, x.UpdatedBy })
                .FirstOrDefaultAsync(ct);
            return Microsoft.AspNetCore.Http.Results.Ok(new CourseMapMetaResponse(
                root.Id,
                courseId,
                map?.Version ?? 0,
                map?.UpdatedAt,
                map?.UpdatedBy));
        });

        app.MapGet("/api/internal/courses/{courseId:guid}/map", async (Guid courseId, EducationDbContext db, CancellationToken ct) =>
        {
            var root = await ResolveRootCourseAsync(courseId, db, ct);
            if (root is null) return Microsoft.AspNetCore.Http.Results.NotFound();
            var map = await db.CourseMaps.AsNoTracking().FirstOrDefaultAsync(x => x.RootCourseId == root.Id, ct);
            return Microsoft.AspNetCore.Http.Results.Ok(new CourseMapResponse(
                root.Id,
                courseId,
                map?.Version ?? 0,
                map is null ? null : ParseDocumentElement(map.DocumentJson),
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
