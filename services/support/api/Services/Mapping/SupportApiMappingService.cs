using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using TaskForge.Support.Api.Data;
using TaskForge.Support.Api.Domain;

using TaskForge.Support.Api.Contracts;
using TaskForge.Support.Api.Hubs;
using static TaskForge.Support.Api.Services.Common.SupportApiCommonService;
using static TaskForge.Support.Api.Services.Serialization.SupportApiSerializationService;

namespace TaskForge.Support.Api.Services.Mapping;

internal static class SupportApiMappingService
{
    internal static async Task<Dictionary<Guid, UserSummaryDto>> LoadUserSummariesAsync(IEnumerable<Guid> userIds, IConfiguration cfg, IHttpClientFactory httpFactory, CancellationToken ct)
    {
        var ids = userIds.Where(x => x != Guid.Empty).Distinct().Take(1000).ToArray();
        if (ids.Length == 0) return new Dictionary<Guid, UserSummaryDto>();
        TaskForgeDebugTrace.UserSummaryRequest("support-api", "identity-api", ids);
        try
        {
            var client = httpFactory.CreateClient();
            using var msg = new HttpRequestMessage(HttpMethod.Post, $"{ServiceUrl(cfg, "IdentityApi", "http://identity-api:8080")}/api/internal/users/summaries")
            {
                Content = JsonContent.Create(new UserIdsRequest(ids), options: JsonOptions())
            };
            AddInternalKey(msg, cfg);
            using var resp = await client.SendAsync(msg, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var empty = new Dictionary<Guid, UserSummaryDto>();
                TaskForgeDebugTrace.UserSummaryResponse("support-api", "identity-api", ids, empty);
                return empty;
            }
            var rows = await resp.Content.ReadFromJsonAsync<List<UserSummaryDto>>(JsonOptions(), ct) ?? new List<UserSummaryDto>();
            var map = rows.Select(x => { x.Normalize(); return x; }).Where(x => x.UserId != Guid.Empty).GroupBy(x => x.UserId).ToDictionary(x => x.Key, x => x.First());
            TaskForgeDebugTrace.UserSummaryResponse("support-api", "identity-api", ids, map);
            return map;
        }
        catch
        {
            var empty = new Dictionary<Guid, UserSummaryDto>();
            TaskForgeDebugTrace.UserSummaryResponse("support-api", "identity-api", ids, empty);
            return empty;
        }
    }

    internal static async Task<Dictionary<Guid, TicketExtra>> LoadTicketExtrasAsync(IEnumerable<Guid> ticketIds, SupportDbContext db, CancellationToken ct)
    {
        var ids = ticketIds.Distinct().ToArray();
        if (ids.Length == 0) return new Dictionary<Guid, TicketExtra>();
        var messages = await db.Messages.AsNoTracking().Where(x => ids.Contains(x.TicketId)).OrderBy(x => x.CreatedAt).ToListAsync(ct);
        return messages.GroupBy(x => x.TicketId).ToDictionary(g => g.Key, g => new TicketExtra(g.Count(), Preview(g.Last().Text)));
    }

    internal static object ToTicketDto(SupportTicket x, TicketExtra? extra, UserSummaryDto? user) => new
    {
        id = x.Id,
        ticketId = x.Id,
        subject = x.Subject,
        title = x.Subject,
        type = TicketType(x.Subject),
        status = x.Status,
        isClosed = IsClosed(x.Status),
        userId = x.UserId,
        user = user == null ? null : new
        {
            id = user.UserId,
            userId = user.UserId,
            login = user.Login,
            user.Email,
            user.MaskedEmail,
            user.FirstName,
            user.LastName,
            displayName = UserLabel(user),
            fullName = UserLabel(user)
        },
        messagesCount = extra?.MessagesCount ?? 0,
        lastMessagePreview = extra?.LastMessagePreview,
        createdAt = x.CreatedAt,
        updatedAt = x.UpdatedAt
    };

    internal static object ToMessageDto(SupportMessage x, UserSummaryDto? user)
    {
        var isAdmin = string.Equals(x.AuthorRole, "admin", StringComparison.OrdinalIgnoreCase);
        return new
        {
            id = x.Id,
            messageId = x.Id,
            ticketId = x.TicketId,
            userId = x.UserId,
            text = x.Text,
            body = x.Text,
            authorRole = x.AuthorRole,
            isFromAdmin = isAdmin,
            authorName = isAdmin ? "Поддержка" : UserLabel(user),
            createdAt = x.CreatedAt,
            createdAtUtc = x.CreatedAt
        };
    }

    internal static string? Preview(string? text)
    {
        var value = (text ?? string.Empty).Trim();
        if (value.Length == 0) return null;
        value = value.Replace("\r", " ").Replace("\n", " ");
        return value.Length <= 160 ? value : value[..157] + "...";
    }

    internal static string UserLabel(UserSummaryDto? user)
    {
        var name = (user?.DisplayName ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(name) && !LooksLikeEmail(name)) return name;
        var full = string.Join(' ', new[] { user?.FirstName, user?.LastName }.Where(x => !string.IsNullOrWhiteSpace(x))).Trim();
        if (!string.IsNullOrWhiteSpace(full)) return full;
        var login = (user?.Login ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(login)) return login;
        var masked = (user?.MaskedEmail ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(masked)) return masked;
        return "Пользователь";
    }

}
