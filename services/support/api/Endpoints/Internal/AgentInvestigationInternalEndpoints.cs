using Microsoft.EntityFrameworkCore;
using TaskForge.Support.Api.Data;

using static TaskForge.Support.Api.Services.Mapping.SupportApiMappingService;

namespace TaskForge.Support.Api.Endpoints;

internal static partial class SupportApiEndpoints
{
    private static WebApplication MapAgentInvestigationInternalEndpoints(WebApplication app)
    {
        app.MapGet("/api/internal/agent/support/{ticketId:guid}", async (
            Guid ticketId,
            SupportDbContext db,
            IConfiguration cfg,
            IHttpClientFactory httpFactory,
            CancellationToken ct) =>
        {
            var ticket = await db.Tickets.AsNoTracking().FirstOrDefaultAsync(x => x.Id == ticketId, ct);
            if (ticket == null)
                return Microsoft.AspNetCore.Http.Results.NotFound(new { message = "Чат не найден.", code = "SUPPORT_CHAT_NOT_FOUND" });

            var messages = await db.Messages.AsNoTracking()
                .Where(x => x.TicketId == ticketId)
                .OrderBy(x => x.CreatedAt)
                .ToListAsync(ct);

            var userIds = messages.Select(x => x.UserId)
                .Concat(new[] { ticket.UserId })
                .Where(x => x.HasValue)
                .Select(x => x!.Value)
                .Distinct()
                .ToArray();
            var users = await LoadUserSummariesAsync(userIds, cfg, httpFactory, ct);
            var byMessageId = messages.ToDictionary(x => x.Id);
            var messageDtos = messages.Select(x =>
            {
                byMessageId.TryGetValue(x.ReplyToMessageId ?? Guid.Empty, out var reply);
                users.TryGetValue(x.UserId ?? Guid.Empty, out var user);
                users.TryGetValue(reply?.UserId ?? Guid.Empty, out var replyUser);
                return ToMessageDto(x, user, reply, replyUser);
            }).ToList();

            users.TryGetValue(ticket.UserId ?? Guid.Empty, out var ticketUser);
            var extra = new TicketExtra(messages.Count, messages.Count == 0 ? null : Preview(messages[^1].Text));
            return Microsoft.AspNetCore.Http.Results.Ok(new
            {
                ticket = ToChatDto(ticket, extra, ticketUser),
                userId = ticket.UserId,
                user = ticketUser == null ? null : UserDto(ticketUser),
                messages = messageDtos,
                messageCount = messages.Count,
                firstMessageAtUtc = messages.Count == 0 ? (DateTimeOffset?)null : messages[0].CreatedAt,
                lastMessageAtUtc = messages.Count == 0 ? (DateTimeOffset?)null : messages[^1].CreatedAt
            });
        });

        return app;
    }
}
