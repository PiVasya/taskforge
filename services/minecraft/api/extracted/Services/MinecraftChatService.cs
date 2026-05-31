using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using taskforge.Data;
using taskforge.Data.Models.Entities;
using taskforge.Hubs;
using taskforge.Services.Interfaces;

namespace taskforge.Services
{
    public sealed class MinecraftChatService : IMinecraftChatService
    {
        private readonly ApplicationDbContext _db;
        private readonly IHubContext<MinecraftChatHub> _hub;

        public MinecraftChatService(ApplicationDbContext db, IHubContext<MinecraftChatHub> hub)
        {
            _db = db;
            _hub = hub;
        }

        public async Task<IReadOnlyList<MinecraftChatMessage>> GetRecentAsync(int take, CancellationToken ct = default)
        {
            take = Math.Clamp(take, 1, 200);
            var raw = await _db.MinecraftChatMessages
                .AsNoTracking()
                .OrderByDescending(x => x.CreatedAtUtc)
                .Take(Math.Min(take * 4, 400))
                .ToListAsync(ct);

            var list = raw
                .Where(x => !IsSuppressedFromFeed(x.Source, x.Message))
                .Take(take)
                .OrderBy(x => x.CreatedAtUtc)
                .ToList();

            return list;
        }

        public async Task<IReadOnlyList<MinecraftChatMessage>> GetOutgoingForMinecraftAsync(DateTime? afterUtc, int take, CancellationToken ct = default)
        {
            take = Math.Clamp(take, 1, 100);
            var q = _db.MinecraftChatMessages
                .AsNoTracking()
                .Where(x => x.Source == "SiteAdmin" || x.Source == "SiteUser");

            if (afterUtc != null)
                q = q.Where(x => x.CreatedAtUtc > afterUtc.Value);

            return await q.OrderBy(x => x.CreatedAtUtc).Take(take).ToListAsync(ct);
        }

        public async Task<MinecraftChatMessage> AddSiteMessageAsync(Guid userId, bool isAdmin, string message, CancellationToken ct = default)
        {
            var user = await _db.Users.FirstOrDefaultAsync(x => x.Id == userId, ct)
                ?? throw new KeyNotFoundException("User not found");

            var text = NormalizeMessage(message);
            var authorName = BuildUserName(user);

            var entity = new MinecraftChatMessage
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                Source = isAdmin ? "SiteAdmin" : "SiteUser",
                AuthorName = authorName,
                MinecraftNick = user.MinecraftNick,
                MinecraftUuid = user.MinecraftUuid,
                Message = text,
                CreatedAtUtc = DateTime.UtcNow
            };

            _db.MinecraftChatMessages.Add(entity);
            await _db.SaveChangesAsync(ct);
            await BroadcastAsync(entity, ct);
            return entity;
        }

        public async Task<MinecraftChatMessage> AddMinecraftMessageAsync(string nick, string? uuid, string message, string? kind = null, CancellationToken ct = default)
        {
            var text = NormalizeMessage(message);
            var cleanNick = (nick ?? string.Empty).Trim();
            if (cleanNick.Length == 0) throw new InvalidOperationException("Nick is required");

            Guid? matchedUserId = null;
            if (!string.IsNullOrWhiteSpace(uuid))
            {
                var userByUuid = await _db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.MinecraftUuid == uuid, ct);
                matchedUserId = userByUuid?.Id;
            }
            if (matchedUserId == null)
            {
                var userByNick = await _db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.MinecraftNick == cleanNick, ct);
                matchedUserId = userByNick?.Id;
            }

            var entity = new MinecraftChatMessage
            {
                Id = Guid.NewGuid(),
                UserId = matchedUserId,
                Source = NormalizeSource(kind),
                AuthorName = cleanNick,
                MinecraftNick = cleanNick,
                MinecraftUuid = string.IsNullOrWhiteSpace(uuid) ? null : uuid.Trim(),
                Message = text,
                CreatedAtUtc = DateTime.UtcNow
            };

            _db.MinecraftChatMessages.Add(entity);
            await _db.SaveChangesAsync(ct);
            await BroadcastAsync(entity, ct);
            return entity;
        }

        private async Task BroadcastAsync(MinecraftChatMessage m, CancellationToken ct)
        {
            var payload = new
            {
                m.Id,
                m.Source,
                m.AuthorName,
                m.MinecraftNick,
                m.MinecraftUuid,
                m.Message,
                m.CreatedAtUtc
            };

            await _hub.Clients.Group("minecraft-chat").SendAsync("ReceiveMessage", payload, ct);
        }


        private static bool IsSuppressedFromFeed(string? source, string? message)
        {
            if (!string.Equals((source ?? string.Empty).Trim(), "MinecraftAdvancement", StringComparison.OrdinalIgnoreCase))
                return false;

            var lower = (message ?? string.Empty).Trim().ToLowerInvariant();
            return lower.Contains("recipes/") || lower.Contains("/root");
        }

        private static string NormalizeSource(string? kind)
        {
            var value = (kind ?? string.Empty).Trim().ToLowerInvariant();
            return value switch
            {
                "join" => "MinecraftJoin",
                "quit" => "MinecraftQuit",
                "advancement" => "MinecraftAdvancement",
                _ => "Minecraft"
            };
        }

        private static string NormalizeMessage(string message)
        {
            var text = (message ?? string.Empty).Trim();
            if (text.Length == 0) throw new InvalidOperationException("Message is empty");
            if (text.Length > 2000) text = text[..2000];
            return text;
        }

        private static string BuildUserName(User user)
        {
            var name = ($"{user.FirstName} {user.LastName}").Trim();
            if (!string.IsNullOrWhiteSpace(name)) return name;
            return user.Email;
        }
    }
}
