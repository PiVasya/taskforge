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
    private const string PersonalChatSubject = "Чат с поддержкой";

    private static WebApplication MapTicketsEndpoints(WebApplication app)
    {
        app.MapGet("/api/support/chat", async (HttpContext http, IConfiguration cfg, SupportDbContext db, IHttpClientFactory httpFactory, CancellationToken ct) =>
        {
            var uid = TaskForgeRequestSecurity.UserId(http, cfg);
            if (uid == null) return Unauthorized();

            var chat = await EnsureUserChatAsync(uid.Value, db, ct);
            var messages = await db.Messages.AsNoTracking().Where(x => x.TicketId == chat.Id).OrderBy(x => x.CreatedAt).ToListAsync(ct);
            var userIds = messages.Select(x => x.UserId).Concat(new[] { chat.UserId }).Where(x => x.HasValue).Select(x => x!.Value).Distinct().ToArray();
            var users = await LoadUserSummariesAsync(userIds, cfg, httpFactory, ct);
            var extra = new TicketExtra(messages.Count, messages.Count == 0 ? null : Preview(messages[^1].Text));

            return Microsoft.AspNetCore.Http.Results.Ok(new
            {
                chat = ToChatDto(chat, extra, chat.UserId.HasValue ? users.GetValueOrDefault(chat.UserId.Value) : null),
                ticket = ToChatDto(chat, extra, chat.UserId.HasValue ? users.GetValueOrDefault(chat.UserId.Value) : null),
                messages = BuildMessageDtos(messages, users)
            });
        });

        app.MapPost("/api/support/chat/messages", async (SupportRequest req, HttpContext http, IConfiguration cfg, SupportDbContext db, IHttpClientFactory httpFactory, IHubContext<SupportHub> hub, CancellationToken ct) =>
        {
            var uid = TaskForgeRequestSecurity.UserId(http, cfg);
            if (uid == null) return Unauthorized();

            var text = (req.Message ?? req.Text ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(text)) return Microsoft.AspNetCore.Http.Results.BadRequest(new { message = "Сообщение не должно быть пустым.", code = "SUPPORT_EMPTY_MESSAGE" });

            var chat = await EnsureUserChatAsync(uid.Value, db, ct);
            var replyTo = await ResolveReplyToAsync(req.ReplyToMessageId, chat.Id, db, ct);
            var now = DateTimeOffset.UtcNow;
            chat.UpdatedAt = now;
            chat.Subject = PersonalChatSubject;

            var msg = new SupportMessage
            {
                TicketId = chat.Id,
                UserId = uid,
                AuthorRole = "user",
                Text = text,
                Source = "Web",
                ReplyToMessageId = replyTo?.Id,
                CreatedAt = now
            };
            db.Messages.Add(msg);
            await db.SaveChangesAsync(ct);

            var userIds = new[] { uid.Value }.Concat(replyTo?.UserId is { } replyUid ? new[] { replyUid } : Array.Empty<Guid>()).Distinct().ToArray();
            var users = await LoadUserSummariesAsync(userIds, cfg, httpFactory, ct);
            var dto = ToMessageDto(msg, users.GetValueOrDefault(uid.Value), replyTo, replyTo?.UserId is { } ruid ? users.GetValueOrDefault(ruid) : null);
            await BroadcastSupportMessageAsync(hub, chat.Id, chat.UserId, dto, ct);

            return Microsoft.AspNetCore.Http.Results.Ok(new { ok = true, chat = ToChatDto(chat, new TicketExtra(0, Preview(text)), users.GetValueOrDefault(uid.Value)), ticket = ToChatDto(chat, new TicketExtra(0, Preview(text)), users.GetValueOrDefault(uid.Value)), message = dto, id = chat.Id, ticketId = chat.Id, chatId = chat.Id, updatedAt = chat.UpdatedAt });
        });

        app.MapGet("/api/support", async (HttpContext http, IConfiguration cfg, SupportDbContext db, IHttpClientFactory httpFactory, CancellationToken ct) =>
        {
            var principal = TaskForgeRequestSecurity.ValidateUser(http, cfg);
            var uid = TaskForgeRequestSecurity.UserId(http, cfg);
            var isAdmin = principal != null && TaskForgeRequestSecurity.HasAnyRole(principal, "Admin");
            if (!isAdmin && uid == null) return Unauthorized();

            if (!isAdmin)
            {
                var chat = await EnsureUserChatAsync(uid!.Value, db, ct);
                var extra = await LoadTicketExtrasAsync(new[] { chat.Id }, db, ct);
                var ownUserSummaries = await LoadUserSummariesAsync(new[] { uid.Value }, cfg, httpFactory, ct);
                return Microsoft.AspNetCore.Http.Results.Ok(new[] { ToChatDto(chat, extra.GetValueOrDefault(chat.Id), ownUserSummaries.GetValueOrDefault(uid.Value)) });
            }

            var tickets = await db.Tickets.AsNoTracking().Where(x => x.UserId != null).OrderByDescending(x => x.UpdatedAt).ToListAsync(ct);
            var onePerUser = tickets
                .GroupBy(x => x.UserId!.Value)
                .Select(g => g.OrderByDescending(x => x.UpdatedAt).ThenByDescending(x => x.CreatedAt).First())
                .OrderByDescending(x => x.UpdatedAt)
                .Take(500)
                .ToList();
            var extras = await LoadTicketExtrasAsync(onePerUser.Select(x => x.Id), db, ct);
            var users = await LoadUserSummariesAsync(onePerUser.Select(x => x.UserId).Where(x => x.HasValue).Select(x => x!.Value), cfg, httpFactory, ct);

            return Microsoft.AspNetCore.Http.Results.Ok(onePerUser.Select(t => ToChatDto(t, extras.GetValueOrDefault(t.Id), t.UserId.HasValue ? users.GetValueOrDefault(t.UserId.Value) : null)).ToList());
        });

        app.MapPost("/api/support", async (SupportRequest req, HttpContext http, IConfiguration cfg, SupportDbContext db, IHttpClientFactory httpFactory, IHubContext<SupportHub> hub, CancellationToken ct) =>
        {
            var uid = TaskForgeRequestSecurity.UserId(http, cfg);
            if (uid == null) return Unauthorized();
            var text = (req.Message ?? req.Text ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(text)) return Microsoft.AspNetCore.Http.Results.BadRequest(new { message = "Сообщение не должно быть пустым.", code = "SUPPORT_EMPTY_MESSAGE" });

            var chat = await EnsureUserChatAsync(uid.Value, db, ct);
            var replyTo = await ResolveReplyToAsync(req.ReplyToMessageId, chat.Id, db, ct);
            var now = DateTimeOffset.UtcNow;
            chat.UpdatedAt = now;
            chat.Subject = PersonalChatSubject;
            var m = new SupportMessage { TicketId = chat.Id, UserId = uid, AuthorRole = "user", Text = text, Source = "Web", ReplyToMessageId = replyTo?.Id, CreatedAt = now };
            db.Messages.Add(m);
            await db.SaveChangesAsync(ct);
            var users = await LoadUserSummariesAsync(new[] { uid.Value }, cfg, httpFactory, ct);
            var ticketDto = ToChatDto(chat, new TicketExtra(1, Preview(text)), users.GetValueOrDefault(uid.Value));
            var dto = ToMessageDto(m, users.GetValueOrDefault(uid.Value), replyTo);
            await BroadcastSupportMessageAsync(hub, chat.Id, chat.UserId, dto, ct);
            return Microsoft.AspNetCore.Http.Results.Ok(new { id = chat.Id, ticketId = chat.Id, chatId = chat.Id, chat = ticketDto, ticket = ticketDto, subject = PersonalChatSubject, createdAt = chat.CreatedAt, updatedAt = chat.UpdatedAt });
        });

        app.MapGet("/api/support/{ticketId:guid}", async (Guid ticketId, HttpContext http, IConfiguration cfg, SupportDbContext db, IHttpClientFactory httpFactory, CancellationToken ct) =>
        {
            var principal = TaskForgeRequestSecurity.ValidateUser(http, cfg);
            var uid = TaskForgeRequestSecurity.UserId(http, cfg);
            var isAdmin = principal != null && TaskForgeRequestSecurity.HasAnyRole(principal, "Admin");
            var t = await db.Tickets.AsNoTracking().FirstOrDefaultAsync(x => x.Id == ticketId, ct);
            if (t == null) return Microsoft.AspNetCore.Http.Results.NotFound(new { message = "Чат не найден.", code = "SUPPORT_CHAT_NOT_FOUND" });
            if (!isAdmin && t.UserId != uid) return Microsoft.AspNetCore.Http.Results.Json(new { message = "Нет доступа к этому чату.", code = "SUPPORT_CHAT_FORBIDDEN" }, statusCode: StatusCodes.Status403Forbidden);

            var messages = await db.Messages.AsNoTracking().Where(x => x.TicketId == ticketId).OrderBy(x => x.CreatedAt).ToListAsync(ct);
            var userIds = messages.Select(x => x.UserId).Concat(new[] { t.UserId }).Where(x => x.HasValue).Select(x => x!.Value).Distinct().ToArray();
            var users = await LoadUserSummariesAsync(userIds, cfg, httpFactory, ct);
            var extra = new TicketExtra(messages.Count, messages.Count == 0 ? null : Preview(messages[^1].Text));
            return Microsoft.AspNetCore.Http.Results.Ok(new
            {
                chat = ToChatDto(t, extra, t.UserId.HasValue ? users.GetValueOrDefault(t.UserId.Value) : null),
                ticket = ToChatDto(t, extra, t.UserId.HasValue ? users.GetValueOrDefault(t.UserId.Value) : null),
                messages = BuildMessageDtos(messages, users)
            });
        });

        app.MapPost("/api/support/{ticketId:guid}", async (Guid ticketId, SupportRequest req, HttpContext http, IConfiguration cfg, SupportDbContext db, IHttpClientFactory httpFactory, IHubContext<SupportHub> hub, CancellationToken ct) =>
        {
            var principal = TaskForgeRequestSecurity.ValidateUser(http, cfg);
            var uid = TaskForgeRequestSecurity.UserId(http, cfg);
            var isAdmin = principal != null && TaskForgeRequestSecurity.HasAnyRole(principal, "Admin");
            var t = await db.Tickets.FindAsync([ticketId], ct);
            if (t == null) return Microsoft.AspNetCore.Http.Results.NotFound(new { message = "Чат не найден.", code = "SUPPORT_CHAT_NOT_FOUND" });
            if (!isAdmin && t.UserId != uid) return Microsoft.AspNetCore.Http.Results.Json(new { message = "Нет доступа к этому чату.", code = "SUPPORT_CHAT_FORBIDDEN" }, statusCode: StatusCodes.Status403Forbidden);

            var text = (req.Message ?? req.Text ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(text)) return Microsoft.AspNetCore.Http.Results.BadRequest(new { message = "Сообщение не должно быть пустым.", code = "SUPPORT_EMPTY_MESSAGE" });
            var replyTo = await ResolveReplyToAsync(req.ReplyToMessageId, t.Id, db, ct);
            var now = DateTimeOffset.UtcNow;
            t.UpdatedAt = now;
            t.Subject = PersonalChatSubject;
            var msg = new SupportMessage { TicketId = ticketId, UserId = uid, AuthorRole = isAdmin ? "admin" : "user", Text = text, Source = "Web", ReplyToMessageId = replyTo?.Id, CreatedAt = now };
            db.Messages.Add(msg);
            await db.SaveChangesAsync(ct);

            var userIds = new List<Guid>();
            if (uid.HasValue) userIds.Add(uid.Value);
            if (replyTo?.UserId is { } replyUid) userIds.Add(replyUid);
            var users = await LoadUserSummariesAsync(userIds, cfg, httpFactory, ct);
            var dto = ToMessageDto(msg, uid.HasValue ? users.GetValueOrDefault(uid.Value) : null, replyTo, replyTo?.UserId is { } ruid ? users.GetValueOrDefault(ruid) : null);
            await BroadcastSupportMessageAsync(hub, ticketId, t.UserId, dto, ct);
            return Microsoft.AspNetCore.Http.Results.Ok(new { chat = ToChatDto(t, new TicketExtra(0, Preview(text)), t.UserId.HasValue ? users.GetValueOrDefault(t.UserId.Value) : null), ticket = ToChatDto(t, new TicketExtra(0, Preview(text)), t.UserId.HasValue ? users.GetValueOrDefault(t.UserId.Value) : null), message = dto, id = t.Id, ticketId = t.Id, chatId = t.Id, updatedAt = t.UpdatedAt });
        });

        app.MapGet("/api/internal/support/analytics/summary", async (DateTimeOffset? fromUtc, DateTimeOffset? toUtc, int days, SupportDbContext db, IHttpClientFactory httpFactory, IConfiguration cfg, CancellationToken ct) =>
        {
            days = System.Math.Clamp(days <= 0 ? 30 : days, 1, 365);
            var to = toUtc ?? DateTimeOffset.UtcNow;
            var from = fromUtc ?? to.AddDays(-days);

            var messages = await db.Messages.AsNoTracking()
                .Where(x => x.CreatedAt >= from && x.CreatedAt <= to)
                .OrderBy(x => x.CreatedAt)
                .ToListAsync(ct);

            var ticketIds = messages.Select(x => x.TicketId).Distinct().ToArray();
            var tickets = ticketIds.Length == 0
                ? new List<SupportTicket>()
                : await db.Tickets.AsNoTracking().Where(x => ticketIds.Contains(x.Id)).ToListAsync(ct);

            var adminMessages = messages.Where(x => string.Equals(x.AuthorRole, "admin", StringComparison.OrdinalIgnoreCase)).ToList();
            var userMessages = messages.Where(x => !string.Equals(x.AuthorRole, "admin", StringComparison.OrdinalIgnoreCase)).ToList();
            var allByTicket = ticketIds.Length == 0
                ? new List<SupportMessage>()
                : await db.Messages.AsNoTracking().Where(x => ticketIds.Contains(x.TicketId) && x.CreatedAt <= to).OrderBy(x => x.CreatedAt).ToListAsync(ct);

            var responseMinutes = userMessages.Select(m =>
                allByTicket.FirstOrDefault(next => next.TicketId == m.TicketId
                    && string.Equals(next.AuthorRole, "admin", StringComparison.OrdinalIgnoreCase)
                    && next.CreatedAt > m.CreatedAt) is { } admin
                    ? (double?)(admin.CreatedAt - m.CreatedAt).TotalMinutes
                    : null)
                .Where(x => x.HasValue)
                .Select(x => x!.Value)
                .ToList();

            var firstResponseMinutes = tickets.Select(t =>
            {
                var firstUser = allByTicket.FirstOrDefault(m => m.TicketId == t.Id && !string.Equals(m.AuthorRole, "admin", StringComparison.OrdinalIgnoreCase));
                if (firstUser == null) return null;
                var firstAdmin = allByTicket.FirstOrDefault(m => m.TicketId == t.Id && string.Equals(m.AuthorRole, "admin", StringComparison.OrdinalIgnoreCase) && m.CreatedAt > firstUser.CreatedAt);
                return firstAdmin == null ? null : (double?)(firstAdmin.CreatedAt - firstUser.CreatedAt).TotalMinutes;
            }).Where(x => x.HasValue).Select(x => x!.Value).ToList();

            var adminIds = adminMessages
                .Where(x => x.UserId.HasValue)
                .Select(x => x.UserId!.Value)
                .Distinct()
                .ToArray();
            var users = await LoadUserSummariesAsync(adminIds, cfg, httpFactory, ct);

            List<object> DayPoints(IEnumerable<SupportMessage> rows)
            {
                var byDay = rows.GroupBy(x => x.CreatedAt.UtcDateTime.Date).ToDictionary(x => x.Key, x => x.Count());
                var start = DateTime.UtcNow.Date.AddDays(-(days - 1));
                return Enumerable.Range(0, days).Select(i =>
                {
                    var day = start.AddDays(i);
                    var count = byDay.GetValueOrDefault(day);
                    return (object)new { label = day.ToString("dd.MM"), date = day.ToString("yyyy-MM-dd"), value = count, count };
                }).ToList();
            }

            var topAdmins = adminMessages
                .Where(x => x.UserId.HasValue)
                .GroupBy(x => x.UserId!.Value)
                .Select(g =>
                {
                    var user = users.GetValueOrDefault(g.Key);
                    return new
                    {
                        userId = g.Key,
                        label = UserLabel(user),
                        email = user?.Email ?? user?.MaskedEmail,
                        value = g.Count(),
                    };
                })
                .OrderByDescending(x => x.value)
                .Take(10)
                .ToList();

            return Microsoft.AspNetCore.Http.Results.Ok(new
            {
                totals = new
                {
                    totalChats = ticketIds.Length,
                    totalTickets = ticketIds.Length,
                    totalMessages = messages.Count,
                    userMessages = userMessages.Count,
                    adminMessages = adminMessages.Count,
                    unansweredMessages = userMessages.Count - responseMinutes.Count,
                    avgFirstResponseMinutes = firstResponseMinutes.Count == 0 ? 0 : System.Math.Round(firstResponseMinutes.Average(), 1),
                    avgResponseMinutes = responseMinutes.Count == 0 ? 0 : System.Math.Round(responseMinutes.Average(), 1),
                },
                userMessagesByDay = DayPoints(userMessages),
                adminMessagesByDay = DayPoints(adminMessages),
                ticketsByDay = DayPoints(userMessages),
                closedByDay = DayPoints(adminMessages),
                ticketTypes = ticketIds.Length == 0 ? Array.Empty<object>() : new[] { new { label = "Чаты", value = ticketIds.Length } }.Cast<object>().ToArray(),
                topAdmins,
            });
        });

        return app;
    }

    private static async Task<SupportTicket> EnsureUserChatAsync(Guid userId, SupportDbContext db, CancellationToken ct)
    {
        var chat = await db.Tickets
            .Where(x => x.UserId == userId)
            .OrderByDescending(x => x.UpdatedAt)
            .ThenByDescending(x => x.CreatedAt)
            .FirstOrDefaultAsync(ct);
        if (chat != null)
        {
            if (!string.Equals(chat.Subject, PersonalChatSubject, StringComparison.Ordinal)) chat.Subject = PersonalChatSubject;
            return chat;
        }

        var now = DateTimeOffset.UtcNow;
        chat = new SupportTicket
        {
            UserId = userId,
            Subject = PersonalChatSubject,
            Status = "open",
            CreatedAt = now,
            UpdatedAt = now
        };
        db.Tickets.Add(chat);
        await db.SaveChangesAsync(ct);
        return chat;
    }

    private static async Task<SupportMessage?> ResolveReplyToAsync(Guid? replyToMessageId, Guid ticketId, SupportDbContext db, CancellationToken ct)
    {
        if (!replyToMessageId.HasValue || replyToMessageId.Value == Guid.Empty) return null;
        return await db.Messages.AsNoTracking().FirstOrDefaultAsync(x => x.Id == replyToMessageId.Value && x.TicketId == ticketId, ct);
    }

    private static List<object> BuildMessageDtos(List<SupportMessage> messages, Dictionary<Guid, UserSummaryDto> users)
    {
        var byId = messages.ToDictionary(x => x.Id, x => x);
        return messages.Select(m =>
        {
            SupportMessage? reply = null;
            if (m.ReplyToMessageId.HasValue) byId.TryGetValue(m.ReplyToMessageId.Value, out reply);
            UserSummaryDto? replyUser = null;
            if (reply?.UserId is { } replyUserId) users.TryGetValue(replyUserId, out replyUser);
            return ToMessageDto(m, m.UserId.HasValue ? users.GetValueOrDefault(m.UserId.Value) : null, reply, replyUser);
        }).ToList();
    }
    private static async Task BroadcastSupportMessageAsync(IHubContext<SupportHub> hub, Guid ticketId, Guid? userId, object dto, CancellationToken ct)
    {
        await hub.Clients.Group(SupportHubGroups.ForTicket(ticketId)).SendAsync("ReceiveMessage", ticketId.ToString(), dto, ct);
        await hub.Clients.Group(SupportHubGroups.ForAdmins()).SendAsync("ReceiveMessage", ticketId.ToString(), dto, ct);
        if (userId.HasValue)
        {
            await hub.Clients.Group(SupportHubGroups.ForUser(userId.Value)).SendAsync("ReceiveMessage", ticketId.ToString(), dto, ct);
        }
    }

}
