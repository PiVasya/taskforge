using System.Security.Claims;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using TaskForge.Education.Api.Data;
using TaskForge.Education.Api.Services.CourseMaps;
using static TaskForge.Education.Api.Services.Access.EducationApiAccessService;
using static TaskForge.Education.Api.Services.Common.EducationApiCommonService;

namespace TaskForge.Education.Api.Hubs;

public sealed class CourseMapPresenceHub : Hub
{
    private const string RootItem = "course-map-root";
    private readonly EducationDbContext _db;
    private readonly IConfiguration _cfg;
    private readonly CourseMapPresenceStore _presence;

    public CourseMapPresenceHub(EducationDbContext db, IConfiguration cfg, CourseMapPresenceStore presence)
    {
        _db = db;
        _cfg = cfg;
        _presence = presence;
    }

    public async Task JoinMap(string courseId, string? displayName = null, string? avatarUrl = null, bool isDirty = false)
    {
        if (!Guid.TryParse(courseId, out var requestedCourseId)) return;
        var http = Context.GetHttpContext();
        if (http is null) return;

        var access = await ResolveAccessContext(http, _cfg, _db, Context.ConnectionAborted);
        if (!access.UserId.HasValue) return;
        var root = await ResolveRootAsync(requestedCourseId, Context.ConnectionAborted);
        if (root is null || !CanEditCourse(access, root)) return;

        if (Context.Items.TryGetValue(RootItem, out var oldValue) && oldValue is Guid oldRoot && oldRoot != root.Id)
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, Group(oldRoot));
            await _presence.RemoveAsync(oldRoot, Context.ConnectionId);
            await BroadcastAsync(oldRoot);
        }

        Context.Items[RootItem] = root.Id;
        await Groups.AddToGroupAsync(Context.ConnectionId, Group(root.Id));
        var safeName = string.IsNullOrWhiteSpace(displayName)
            ? Context.User?.FindFirstValue(ClaimTypes.Name) ?? "Пользователь"
            : displayName;
        var rows = await _presence.UpsertAsync(root.Id, Context.ConnectionId, access.UserId.Value, safeName!, avatarUrl, isDirty);
        await Clients.Group(Group(root.Id)).SendAsync("PresenceChanged", rows, Context.ConnectionAborted);
    }

    public async Task Heartbeat(string courseId, bool isDirty = false, string? displayName = null, string? avatarUrl = null)
    {
        if (!Guid.TryParse(courseId, out var rootId)) return;
        if (!Context.Items.TryGetValue(RootItem, out var current) || current is not Guid joinedRoot || joinedRoot != rootId) return;
        var http = Context.GetHttpContext();
        if (http is null) return;
        var userId = TaskForgeRequestSecurity.UserId(http, _cfg);
        if (!userId.HasValue) return;
        var safeName = string.IsNullOrWhiteSpace(displayName)
            ? Context.User?.FindFirstValue(ClaimTypes.Name) ?? "Пользователь"
            : displayName;
        var rows = await _presence.UpsertAsync(rootId, Context.ConnectionId, userId.Value, safeName!, avatarUrl, isDirty);
        await Clients.Group(Group(rootId)).SendAsync("PresenceChanged", rows, Context.ConnectionAborted);
    }

    public async Task LeaveMap(string courseId)
    {
        if (!Guid.TryParse(courseId, out var rootId)) return;
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, Group(rootId));
        await _presence.RemoveAsync(rootId, Context.ConnectionId);
        Context.Items.Remove(RootItem);
        await BroadcastAsync(rootId);
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (Context.Items.TryGetValue(RootItem, out var value) && value is Guid rootId)
        {
            await _presence.RemoveAsync(rootId, Context.ConnectionId);
            await BroadcastAsync(rootId);
        }
        await base.OnDisconnectedAsync(exception);
    }

    private async Task BroadcastAsync(Guid rootId)
    {
        var rows = await _presence.GetAsync(rootId);
        await Clients.Group(Group(rootId)).SendAsync("PresenceChanged", rows);
    }

    private async Task<TaskForge.Education.Api.Domain.Course?> ResolveRootAsync(Guid requestedCourseId, CancellationToken ct)
    {
        var courses = await _db.Courses.AsNoTracking()
            .Select(x => new { x.Id, x.ParentCourseId })
            .ToListAsync(ct);
        var parents = courses.ToDictionary(x => x.Id, x => x.ParentCourseId);
        if (!parents.ContainsKey(requestedCourseId)) return null;
        var current = requestedCourseId;
        var seen = new HashSet<Guid>();
        while (parents.TryGetValue(current, out var parent) && parent.HasValue)
        {
            if (!seen.Add(current)) return null;
            current = parent.Value;
        }
        return await _db.Courses.AsNoTracking().FirstOrDefaultAsync(x => x.Id == current, ct);
    }

    public static string Group(Guid rootCourseId) => $"course-map-{rootCourseId:N}";
}
