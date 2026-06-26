using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using TaskForge.Support.Api.Data;
using TaskForge.Support.Api.Domain;


namespace TaskForge.Support.Api.Hubs;

public sealed class SupportHub : Hub
{
    private readonly SupportDbContext _db;
    private readonly IConfiguration _cfg;

    public SupportHub(SupportDbContext db, IConfiguration cfg)
    {
        _db = db;
        _cfg = cfg;
    }

    public async Task JoinTicket(string ticketId)
    {
        if (!Guid.TryParse(ticketId, out var id)) return;

        var http = Context.GetHttpContext();
        var uid = http == null ? null : TaskForgeRequestSecurity.UserId(http, _cfg);
        var isAdmin = Context.User?.Identity?.IsAuthenticated == true && TaskForgeRequestSecurity.HasAnyRole(Context.User, "Admin");
        if (!isAdmin && !uid.HasValue) return;

        var allowed = await _db.Tickets.AsNoTracking().AnyAsync(x => x.Id == id && (isAdmin || x.UserId == uid), Context.ConnectionAborted);
        if (!allowed) return;

        await Groups.AddToGroupAsync(Context.ConnectionId, SupportHubGroups.ForTicket(id));
    }


    public async Task JoinUser()
    {
        var http = Context.GetHttpContext();
        var uid = http == null ? null : TaskForgeRequestSecurity.UserId(http, _cfg);
        if (!uid.HasValue) return;
        await Groups.AddToGroupAsync(Context.ConnectionId, SupportHubGroups.ForUser(uid.Value));
    }

    public async Task JoinAdmins()
    {
        if (Context.User?.Identity?.IsAuthenticated != true || !TaskForgeRequestSecurity.HasAnyRole(Context.User, "Admin")) return;
        await Groups.AddToGroupAsync(Context.ConnectionId, SupportHubGroups.ForAdmins());
    }

    public Task LeaveTicket(string ticketId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, SupportHubGroups.FromString(ticketId));
}

public static class SupportHubGroups
{
    public static string ForTicket(Guid id) => $"support-ticket-{id:N}";
    public static string ForUser(Guid userId) => $"support-user-{userId:N}";
    public static string ForAdmins() => "support-admins";
    public static string FromString(string? id) => Guid.TryParse(id, out var guid) ? ForTicket(guid) : $"support-ticket-{id}";
}
