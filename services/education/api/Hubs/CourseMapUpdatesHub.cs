using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using TaskForge.Education.Api.Data;
using static TaskForge.Education.Api.Services.Access.EducationApiAccessService;
using static TaskForge.Education.Api.Services.Common.EducationApiCommonService;

namespace TaskForge.Education.Api.Hubs;

/// <summary>
/// MapSaved notifications only. No membership list, user tracking, Redis
/// presence keys, heartbeats or online status. Version checks protect writes.
/// </summary>
public sealed class CourseMapUpdatesHub(EducationDbContext db, IConfiguration cfg) : Hub
{
    private const string RootItem = "course-map-root";

    public async Task JoinMap(string courseId)
    {
        if (!Guid.TryParse(courseId, out var courseGuid)) return;
        var http = Context.GetHttpContext();
        if (http is null) return;
        var access = await ResolveAccessContext(http, cfg, db, Context.ConnectionAborted);
        if (!access.UserId.HasValue) return;
        var courses = await db.Courses.AsNoTracking()
            .Select(x => new { x.Id, x.ParentCourseId }).ToListAsync(Context.ConnectionAborted);
        var parents = courses.ToDictionary(x => x.Id, x => x.ParentCourseId);
        if (!parents.ContainsKey(courseGuid)) return;
        var rootId = courseGuid;
        var visited = new HashSet<Guid>();
        while (parents.TryGetValue(rootId, out var parent) && parent.HasValue)
        {
            if (!visited.Add(rootId)) return;
            rootId = parent.Value;
        }
        var root = await db.Courses.AsNoTracking().FirstOrDefaultAsync(x => x.Id == rootId, Context.ConnectionAborted);
        if (root is null || !CanEditCourse(access, root)) return;
        if (Context.Items.TryGetValue(RootItem, out var old) && old is Guid previous && previous != rootId)
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, Group(previous));
        Context.Items[RootItem] = rootId;
        await Groups.AddToGroupAsync(Context.ConnectionId, Group(rootId));
    }

    public async Task LeaveMap(string courseId)
    {
        if (!Guid.TryParse(courseId, out var requested)) return;
        if (Context.Items.TryGetValue(RootItem, out var current) && current is Guid root && root == requested)
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, Group(root));
            Context.Items.Remove(RootItem);
        }
    }

    public static string Group(Guid rootCourseId) => $"course-map-{rootCourseId:N}";
}
