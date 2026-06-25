using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using TaskForge.Support.Api.Data;
using TaskForge.Support.Api.Domain;

using TaskForge.Support.Api.Contracts;
using TaskForge.Support.Api.Hubs;
using static TaskForge.Support.Api.Services.Common.SupportApiCommonService;
using static TaskForge.Support.Api.Services.Mapping.SupportApiMappingService;
using static TaskForge.Support.Api.Services.Serialization.SupportApiSerializationService;

namespace TaskForge.Support.Api.Endpoints;

internal static partial class SupportApiEndpoints
{
    private static WebApplication MapTicketsEndpoints(WebApplication app)
    {
        app.MapGet("/api/support", async (HttpContext http, IConfiguration cfg, SupportDbContext db, IHttpClientFactory httpFactory, CancellationToken ct) =>
        {
            var principal = TaskForgeRequestSecurity.ValidateUser(http, cfg);
            var uid = TaskForgeRequestSecurity.UserId(http, cfg);
            var isAdmin = principal != null && TaskForgeRequestSecurity.HasAnyRole(principal, "Admin");
            if (!isAdmin && uid == null) return Unauthorized();

            var query = db.Tickets.AsNoTracking();
            if (!isAdmin) query = query.Where(x => x.UserId == uid);

            var tickets = await query.OrderByDescending(x => x.UpdatedAt).Take(500).ToListAsync(ct);
            var extras = await LoadTicketExtrasAsync(tickets.Select(x => x.Id), db, ct);
            var users = await LoadUserSummariesAsync(tickets.Select(x => x.UserId).Where(x => x.HasValue).Select(x => x!.Value), cfg, httpFactory, ct);

            return Microsoft.AspNetCore.Http.Results.Ok(tickets.Select(t => ToTicketDto(t, extras.GetValueOrDefault(t.Id), t.UserId.HasValue ? users.GetValueOrDefault(t.UserId.Value) : null)).ToList());
        });

        app.MapPost("/api/support", async (SupportRequest req, HttpContext http, IConfiguration cfg, SupportDbContext db, IHttpClientFactory httpFactory, IHubContext<SupportHub> hub, CancellationToken ct) =>
        {
            var uid = TaskForgeRequestSecurity.UserId(http, cfg);
            if (uid == null) return Unauthorized();
            var subject = string.IsNullOrWhiteSpace(req.Subject) ? "Обращение" : req.Subject.Trim();
            var text = req.Message ?? req.Text ?? string.Empty;
            var now = DateTimeOffset.UtcNow;
            var t = new SupportTicket { UserId = uid, Subject = subject, Status = "open", CreatedAt = now, UpdatedAt = now };
            var m = new SupportMessage { TicketId = t.Id, UserId = uid, AuthorRole = "user", Text = text, Source = "Web", CreatedAt = now };
            db.Tickets.Add(t);
            db.Messages.Add(m);
            await db.SaveChangesAsync(ct);
            var users = await LoadUserSummariesAsync(new[] { uid.Value }, cfg, httpFactory, ct);
            var ticketDto = ToTicketDto(t, new TicketExtra(1, Preview(text)), users.GetValueOrDefault(uid.Value));
            await hub.Clients.Group(SupportHubGroups.ForTicket(t.Id)).SendAsync("ReceiveMessage", t.Id.ToString(), ToMessageDto(m, users.GetValueOrDefault(uid.Value)), ct);
            return Microsoft.AspNetCore.Http.Results.Ok(new { id = t.Id, ticketId = t.Id, ticket = ticketDto, subject = t.Subject, status = t.Status, createdAt = t.CreatedAt, updatedAt = t.UpdatedAt });
        });

        app.MapGet("/api/support/{ticketId:guid}", async (Guid ticketId, HttpContext http, IConfiguration cfg, SupportDbContext db, IHttpClientFactory httpFactory, CancellationToken ct) =>
        {
            var principal = TaskForgeRequestSecurity.ValidateUser(http, cfg);
            var uid = TaskForgeRequestSecurity.UserId(http, cfg);
            var isAdmin = principal != null && TaskForgeRequestSecurity.HasAnyRole(principal, "Admin");
            var t = await db.Tickets.AsNoTracking().FirstOrDefaultAsync(x => x.Id == ticketId, ct);
            if (t == null) return Microsoft.AspNetCore.Http.Results.NotFound(new { message = "Обращение не найдено.", code = "SUPPORT_TICKET_NOT_FOUND" });
            if (!isAdmin && t.UserId != uid) return Microsoft.AspNetCore.Http.Results.Json(new { message = "Нет доступа к этому обращению.", code = "SUPPORT_TICKET_FORBIDDEN" }, statusCode: StatusCodes.Status403Forbidden);

            var messages = await db.Messages.AsNoTracking().Where(x => x.TicketId == ticketId).OrderBy(x => x.CreatedAt).ToListAsync(ct);
            var userIds = messages.Select(x => x.UserId).Concat(new[] { t.UserId }).Where(x => x.HasValue).Select(x => x!.Value).Distinct().ToArray();
            var users = await LoadUserSummariesAsync(userIds, cfg, httpFactory, ct);
            var extra = new TicketExtra(messages.Count, messages.Count == 0 ? null : Preview(messages[^1].Text));
            return Microsoft.AspNetCore.Http.Results.Ok(new
            {
                ticket = ToTicketDto(t, extra, t.UserId.HasValue ? users.GetValueOrDefault(t.UserId.Value) : null),
                messages = messages.Select(m => ToMessageDto(m, m.UserId.HasValue ? users.GetValueOrDefault(m.UserId.Value) : null)).ToList()
            });
        });

        app.MapPost("/api/support/{ticketId:guid}", async (Guid ticketId, SupportRequest req, HttpContext http, IConfiguration cfg, SupportDbContext db, IHttpClientFactory httpFactory, IHubContext<SupportHub> hub, CancellationToken ct) =>
        {
            var principal = TaskForgeRequestSecurity.ValidateUser(http, cfg);
            var uid = TaskForgeRequestSecurity.UserId(http, cfg);
            var isAdmin = principal != null && TaskForgeRequestSecurity.HasAnyRole(principal, "Admin");
            var t = await db.Tickets.FindAsync([ticketId], ct);
            if (t == null) return Microsoft.AspNetCore.Http.Results.NotFound(new { message = "Обращение не найдено.", code = "SUPPORT_TICKET_NOT_FOUND" });
            if (!isAdmin && t.UserId != uid) return Microsoft.AspNetCore.Http.Results.Json(new { message = "Нет доступа к этому обращению.", code = "SUPPORT_TICKET_FORBIDDEN" }, statusCode: StatusCodes.Status403Forbidden);

            var text = req.Message ?? req.Text ?? string.Empty;
            var now = DateTimeOffset.UtcNow;
            t.UpdatedAt = now;
            if (isAdmin && string.Equals(t.Status, "open", StringComparison.OrdinalIgnoreCase)) t.Status = "in-progress";
            var msg = new SupportMessage { TicketId = ticketId, UserId = uid, AuthorRole = isAdmin ? "admin" : "user", Text = text, Source = "Web", CreatedAt = now };
            db.Messages.Add(msg);
            await db.SaveChangesAsync(ct);

            var users = uid.HasValue ? await LoadUserSummariesAsync(new[] { uid.Value }, cfg, httpFactory, ct) : new Dictionary<Guid, UserSummaryDto>();
            var dto = ToMessageDto(msg, uid.HasValue ? users.GetValueOrDefault(uid.Value) : null);
            await hub.Clients.Group(SupportHubGroups.ForTicket(ticketId)).SendAsync("ReceiveMessage", ticketId.ToString(), dto, ct);
            return Microsoft.AspNetCore.Http.Results.Ok(new { ticket = ToTicketDto(t, new TicketExtra(0, Preview(text)), (UserSummaryDto?)null), message = dto, id = t.Id, ticketId = t.Id, status = t.Status, updatedAt = t.UpdatedAt });
        });


        app.MapGet("/api/internal/support/analytics/summary", async (DateTimeOffset? fromUtc, DateTimeOffset? toUtc, int days, SupportDbContext db, IHttpClientFactory httpFactory, IConfiguration cfg, CancellationToken ct) =>
        {
            days = System.Math.Clamp(days <= 0 ? 30 : days, 1, 365);
            var to = toUtc ?? DateTimeOffset.UtcNow;
            var from = fromUtc ?? to.AddDays(-days);

            var tickets = await db.Tickets.AsNoTracking()
                .Where(x => x.CreatedAt >= from && x.CreatedAt <= to)
                .ToListAsync(ct);

            var ticketIds = tickets.Select(x => x.Id).ToArray();
            var messages = ticketIds.Length == 0
                ? new List<SupportMessage>()
                : await db.Messages.AsNoTracking()
                    .Where(x => ticketIds.Contains(x.TicketId))
                    .OrderBy(x => x.CreatedAt)
                    .ToListAsync(ct);

            var firstResponseMinutes = tickets.Select(t =>
                messages.FirstOrDefault(m => m.TicketId == t.Id && string.Equals(m.AuthorRole, "admin", StringComparison.OrdinalIgnoreCase) && m.CreatedAt >= t.CreatedAt) is { } firstAdmin
                    ? (double?)(firstAdmin.CreatedAt - t.CreatedAt).TotalMinutes
                    : null)
                .Where(x => x.HasValue)
                .Select(x => x!.Value)
                .ToList();

            var closedTickets = tickets.Where(x => IsClosed(x.Status)).ToList();
            var closeMinutes = closedTickets
                .Select(x => System.Math.Max(0, (x.UpdatedAt - x.CreatedAt).TotalMinutes))
                .ToList();

            var adminIds = messages
                .Where(x => string.Equals(x.AuthorRole, "admin", StringComparison.OrdinalIgnoreCase) && x.UserId.HasValue)
                .Select(x => x.UserId!.Value)
                .Distinct()
                .ToArray();
            var users = await LoadUserSummariesAsync(adminIds, cfg, httpFactory, ct);

            var byDay = tickets.GroupBy(x => x.CreatedAt.UtcDateTime.Date).ToDictionary(x => x.Key, x => x.Count());
            var closedByDay = closedTickets.GroupBy(x => x.UpdatedAt.UtcDateTime.Date).ToDictionary(x => x.Key, x => x.Count());
            var start = DateTime.UtcNow.Date.AddDays(-(days - 1));
            var ticketPoints = Enumerable.Range(0, days).Select(i =>
            {
                var day = start.AddDays(i);
                var count = byDay.GetValueOrDefault(day);
                return new { label = day.ToString("dd.MM"), date = day.ToString("yyyy-MM-dd"), value = count, count };
            }).ToList();
            var closedPoints = Enumerable.Range(0, days).Select(i =>
            {
                var day = start.AddDays(i);
                var count = closedByDay.GetValueOrDefault(day);
                return new { label = day.ToString("dd.MM"), date = day.ToString("yyyy-MM-dd"), value = count, count };
            }).ToList();

            var topAdmins = messages
                .Where(x => string.Equals(x.AuthorRole, "admin", StringComparison.OrdinalIgnoreCase))
                .GroupBy(x => x.UserId)
                .Select(g =>
                {
                    var user = g.Key.HasValue ? users.GetValueOrDefault(g.Key.Value) : null;
                    return new
                    {
                        userId = g.Key,
                        label = UserLabel(user),
                        email = user?.Email ?? user?.MaskedEmail,
                        value = g.Count()
                    };
                })
                .OrderByDescending(x => x.value)
                .Take(10)
                .ToList();

            return Microsoft.AspNetCore.Http.Results.Ok(new
            {
                totals = new
                {
                    totalTickets = tickets.Count,
                    openTickets = tickets.Count(x => !IsClosed(x.Status)),
                    closedTickets = closedTickets.Count,
                    avgFirstResponseMinutes = firstResponseMinutes.Count == 0 ? 0 : System.Math.Round(firstResponseMinutes.Average(), 1),
                    avgCloseMinutes = closeMinutes.Count == 0 ? 0 : System.Math.Round(closeMinutes.Average(), 1),
                },
                ticketsByDay = ticketPoints,
                closedByDay = closedPoints,
                ticketTypes = tickets.GroupBy(x => string.IsNullOrWhiteSpace(x.Status) ? "unknown" : x.Status).Select(g => new { label = g.Key, value = g.Count() }).OrderByDescending(x => x.value).ToList(),
                topAdmins,
            });
        });

        return app;
    }
}
