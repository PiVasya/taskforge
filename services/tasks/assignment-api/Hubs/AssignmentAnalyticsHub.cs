using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using TaskForge.Tasks.Api.Data;
using TaskForge.Tasks.Api.Services.Access;

namespace TaskForge.Tasks.Api.Hubs;

public sealed class AssignmentAnalyticsHub : Hub
{
    private readonly TasksDbContext _db;
    private readonly IConfiguration _cfg;
    private readonly IHttpClientFactory _httpFactory;

    public AssignmentAnalyticsHub(TasksDbContext db, IConfiguration cfg, IHttpClientFactory httpFactory)
    {
        _db = db;
        _cfg = cfg;
        _httpFactory = httpFactory;
    }

    public async Task JoinAssignment(string assignmentId)
    {
        if (!Guid.TryParse(assignmentId, out var id)) return;
        var http = Context.GetHttpContext();
        if (http == null) return;
        if (!AssignmentApiAccessService.IsEditor(http, _cfg)) return;
        var exists = await _db.Assignments.AsNoTracking().AnyAsync(x => x.Id == id, Context.ConnectionAborted);
        if (!exists) return;
        await Groups.AddToGroupAsync(Context.ConnectionId, AssignmentAnalyticsHubGroups.ForAssignment(id));
    }

    public Task LeaveAssignment(string assignmentId)
    {
        return Groups.RemoveFromGroupAsync(Context.ConnectionId, AssignmentAnalyticsHubGroups.FromString(assignmentId));
    }
}

public static class AssignmentAnalyticsHubGroups
{
    public static string ForAssignment(Guid id) => $"assignment-analytics-{id:N}";
    public static string FromString(string? id) => Guid.TryParse(id, out var guid) ? ForAssignment(guid) : $"assignment-analytics-{id}";
}
