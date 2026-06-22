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
    private static WebApplication MapIntegrationsEndpoints(WebApplication app)
    {
        app.MapGet("/api/telegram/status", () => Microsoft.AspNetCore.Http.Results.Ok(new { configured = true }));

        app.MapGet("/api/internal/support/telegram/pending-user-messages", async (SupportDbContext db, IConfiguration cfg, IHttpClientFactory httpFactory, CancellationToken ct) =>
        {
            var rows = await db.Messages.AsNoTracking()
                .Where(x => x.AuthorRole != "admin" && x.TelegramMessageId == null && (x.Source == "Web" || x.Source == "TelegramUser"))
                .OrderBy(x => x.CreatedAt)
                .Take(100)
                .ToListAsync(ct);

            var ticketIds = rows.Select(x => x.TicketId).Distinct().ToArray();
            var tickets = await db.Tickets.AsNoTracking().Where(x => ticketIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
            var userIds = tickets.Values.Select(x => x.UserId).Concat(rows.Select(x => x.UserId)).Where(x => x.HasValue).Select(x => x!.Value).Distinct().ToArray();
            var users = await LoadUserSummariesAsync(userIds, cfg, httpFactory, ct);

            return Microsoft.AspNetCore.Http.Results.Ok(rows.Select(m =>
            {
                tickets.TryGetValue(m.TicketId, out var ticket);
                var uid = m.UserId ?? ticket?.UserId;
                var user = uid.HasValue ? users.GetValueOrDefault(uid.Value) : null;
                return new
                {
                    messageId = m.Id,
                    ticketId = m.TicketId,
                    subject = ticket?.Subject ?? "Обращение",
                    status = ticket?.Status ?? "open",
                    userId = uid,
                    user = user == null ? null : new
                    {
                        id = user.UserId,
                        userId = user.UserId,
                        user.Login,
                        user.Email,
                        user.MaskedEmail,
                        user.FirstName,
                        user.LastName,
                        displayName = UserLabel(user)
                    },
                    text = m.Text,
                    source = m.Source,
                    createdAt = m.CreatedAt
                };
            }).ToList());
        });

        app.MapPost("/api/internal/support/telegram/messages/{messageId:guid}/mark-sent", async (Guid messageId, TelegramMarkMessageRequest req, SupportDbContext db, CancellationToken ct) =>
        {
            var msg = await db.Messages.FindAsync([messageId], ct);
            if (msg == null) return Microsoft.AspNetCore.Http.Results.NotFound(new { message = "Сообщение не найдено." });
            msg.TelegramChatId = req.ChatId;
            msg.TelegramMessageId = req.MessageId;
            msg.Source ??= "Web";
            await db.SaveChangesAsync(ct);
            return Microsoft.AspNetCore.Http.Results.Ok(new { ok = true });
        });

        app.MapGet("/api/internal/support/telegram/messages/by-telegram/{telegramMessageId:int}", async (int telegramMessageId, long? chatId, SupportDbContext db, CancellationToken ct) =>
        {
            var query = db.Messages.AsNoTracking().Where(x => x.TelegramMessageId == telegramMessageId);
            if (chatId.HasValue) query = query.Where(x => x.TelegramChatId == chatId.Value);
            var msg = await query.OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync(ct);
            if (msg == null) return Microsoft.AspNetCore.Http.Results.NotFound(new { message = "Связанное сообщение не найдено." });
            return Microsoft.AspNetCore.Http.Results.Ok(new { messageId = msg.Id, ticketId = msg.TicketId });
        });

        app.MapGet("/api/internal/support/telegram/pending-admin-replies", async (SupportDbContext db, CancellationToken ct) =>
        {
            var rows = await db.Messages.AsNoTracking()
                .Where(x => x.AuthorRole == "admin" && (x.Source == "Web" || x.Source == "TelegramGroup"))
                .OrderBy(x => x.CreatedAt)
                .Take(100)
                .ToListAsync(ct);

            var ticketIds = rows.Select(x => x.TicketId).Distinct().ToArray();
            var tickets = await db.Tickets.AsNoTracking().Where(x => ticketIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);

            return Microsoft.AspNetCore.Http.Results.Ok(rows.Select(m =>
            {
                tickets.TryGetValue(m.TicketId, out var ticket);
                return new
                {
                    messageId = m.Id,
                    ticketId = m.TicketId,
                    userId = ticket?.UserId ?? m.UserId,
                    subject = ticket?.Subject ?? "Обращение",
                    text = m.Text,
                    source = m.Source,
                    createdAt = m.CreatedAt
                };
            }).Where(x => x.userId != null).ToList());
        });

        app.MapPost("/api/internal/support/telegram/admin-replies/{messageId:guid}/mark-delivered", async (Guid messageId, TelegramMarkMessageRequest req, SupportDbContext db, CancellationToken ct) =>
        {
            var msg = await db.Messages.FindAsync([messageId], ct);
            if (msg == null) return Microsoft.AspNetCore.Http.Results.NotFound(new { message = "Сообщение не найдено." });
            msg.TelegramChatId = req.ChatId;
            msg.TelegramMessageId = req.MessageId;
            msg.Source = "TelegramUserPrivate";
            await db.SaveChangesAsync(ct);
            return Microsoft.AspNetCore.Http.Results.Ok(new { ok = true });
        });

        app.MapPost("/api/internal/support/telegram/admin-replies/{messageId:guid}/mark-unavailable", async (Guid messageId, SupportDbContext db, CancellationToken ct) =>
        {
            var msg = await db.Messages.FindAsync([messageId], ct);
            if (msg == null) return Microsoft.AspNetCore.Http.Results.NotFound(new { message = "Сообщение не найдено." });
            msg.Source = "TelegramNoLinkedUser";
            await db.SaveChangesAsync(ct);
            return Microsoft.AspNetCore.Http.Results.Ok(new { ok = true });
        });

        app.MapPost("/api/internal/support/telegram/admin-reply", async (TelegramAdminReplyRequest req, SupportDbContext db, IHttpClientFactory httpFactory, IConfiguration cfg, IHubContext<SupportHub> hub, CancellationToken ct) =>
        {
            var text = (req.Message ?? string.Empty).Trim();
            if (req.TicketId == Guid.Empty || string.IsNullOrWhiteSpace(text))
            {
                return Microsoft.AspNetCore.Http.Results.BadRequest(new { message = "Пустой ответ поддержки." });
            }

            var ticket = await db.Tickets.FindAsync([req.TicketId], ct);
            if (ticket == null) return Microsoft.AspNetCore.Http.Results.NotFound(new { message = "Обращение не найдено." });

            var now = DateTimeOffset.UtcNow;
            ticket.UpdatedAt = now;
            if (string.Equals(ticket.Status, "open", StringComparison.OrdinalIgnoreCase)) ticket.Status = "in-progress";
            var msg = new SupportMessage
            {
                TicketId = ticket.Id,
                UserId = null,
                AuthorRole = "admin",
                Text = text,
                Source = "TelegramGroup",
                TelegramChatId = req.TelegramChatId,
                TelegramMessageId = req.TelegramMessageId,
                CreatedAt = now
            };
            db.Messages.Add(msg);
            await db.SaveChangesAsync(ct);

            var dto = ToMessageDto(msg, null);
            await hub.Clients.Group(SupportHubGroups.ForTicket(ticket.Id)).SendAsync("ReceiveMessage", ticket.Id.ToString(), dto, ct);
            return Microsoft.AspNetCore.Http.Results.Ok(new { ok = true, messageId = msg.Id, ticketId = ticket.Id });
        });

        app.MapPost("/api/internal/support/telegram/user-message", async (TelegramUserMessageRequest req, SupportDbContext db, IHttpClientFactory httpFactory, IConfiguration cfg, IHubContext<SupportHub> hub, CancellationToken ct) =>
        {
            var text = (req.Message ?? string.Empty).Trim();
            if (req.UserId == Guid.Empty || string.IsNullOrWhiteSpace(text))
            {
                return Microsoft.AspNetCore.Http.Results.BadRequest(new { message = "Пустое сообщение поддержки." });
            }

            SupportTicket? ticket = null;
            if (!req.ForceNewTicket)
            {
                var since = DateTimeOffset.UtcNow.AddHours(-24);
                ticket = await db.Tickets
                    .Where(x => x.UserId == req.UserId && x.Status != "closed" && x.UpdatedAt >= since)
                    .OrderByDescending(x => x.UpdatedAt)
                    .FirstOrDefaultAsync(ct);
            }

            var now = DateTimeOffset.UtcNow;
            if (ticket == null)
            {
                ticket = new SupportTicket
                {
                    UserId = req.UserId,
                    Subject = "Telegram",
                    Status = "open",
                    CreatedAt = now,
                    UpdatedAt = now
                };
                db.Tickets.Add(ticket);
            }
            else
            {
                ticket.UpdatedAt = now;
            }

            var msg = new SupportMessage
            {
                TicketId = ticket.Id,
                UserId = req.UserId,
                AuthorRole = "user",
                Text = text,
                Source = "TelegramUser",
                CreatedAt = now
            };
            db.Messages.Add(msg);
            await db.SaveChangesAsync(ct);

            var users = await LoadUserSummariesAsync(new[] { req.UserId }, cfg, httpFactory, ct);
            await hub.Clients.Group(SupportHubGroups.ForTicket(ticket.Id)).SendAsync("ReceiveMessage", ticket.Id.ToString(), ToMessageDto(msg, users.GetValueOrDefault(req.UserId)), ct);
            return Microsoft.AspNetCore.Http.Results.Ok(new { ok = true, ticketId = ticket.Id, messageId = msg.Id });
        });

        return app;
    }
}
