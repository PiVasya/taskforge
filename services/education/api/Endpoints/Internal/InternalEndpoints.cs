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
                return courses.Select(x => new CourseMetadataDto(x.Id, x.Id, x.Title, x.Title, x.Description, x.IsPublic)).ToList();
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
            var course = await db.Courses.AsNoTracking().FirstOrDefaultAsync(x => x.Id == courseId, ct);
            if (course == null) return Microsoft.AspNetCore.Http.Results.NotFound();

            var groupIds = await db.GroupMembers.AsNoTracking().Where(x => x.UserId == userId).Select(x => x.GroupId).ToListAsync(ct);
            var access = new EducationAccessContext(userId, false, groupIds.ToHashSet());
            return Microsoft.AspNetCore.Http.Results.Ok(new { courseId, userId, canView = CanViewCourse(access, course), canEdit = CanEditCourse(access, course), isPublic = course.IsPublic });
        });

        app.MapGet("/api/internal/courses/{courseId:guid}/tree", async (Guid courseId, EducationDbContext db, CancellationToken ct) =>
        {
            var rows = await db.Courses.AsNoTracking()
                .Select(x => new { x.Id, x.ParentCourseId })
                .ToListAsync(ct);

            if (!rows.Any(x => x.Id == courseId)) return Microsoft.AspNetCore.Http.Results.NotFound();

            var children = rows
                .Where(x => x.ParentCourseId.HasValue)
                .GroupBy(x => x.ParentCourseId!.Value)
                .ToDictionary(g => g.Key, g => g.Select(x => x.Id).ToList());

            var result = new List<Guid>();
            var seen = new HashSet<Guid>();
            var queue = new Queue<Guid>();
            queue.Enqueue(courseId);

            while (queue.Count > 0)
            {
                var id = queue.Dequeue();
                if (!seen.Add(id)) continue;
                result.Add(id);
                if (!children.TryGetValue(id, out var directChildren)) continue;
                foreach (var childId in directChildren) queue.Enqueue(childId);
            }

            return Microsoft.AspNetCore.Http.Results.Ok(new { courseId, courseIds = result });
        });

        return app;
    }
}
