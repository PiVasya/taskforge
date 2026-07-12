using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using TaskForge.Minecraft.Api.Contracts;
using TaskForge.Minecraft.Api.Data;
using TaskForge.Minecraft.Api.Domain;
using TaskForge.Minecraft.Api.Hubs;
using static TaskForge.Minecraft.Api.Services.Common.MinecraftApiCommonService;

namespace TaskForge.Minecraft.Api.Endpoints;

internal static partial class MinecraftApiEndpoints
{
    private static WebApplication MapChatEndpoints(WebApplication app)
    {
        app.MapGet("/api/integrations/minecraft/chat/meta", async (HttpContext http, IConfiguration cfg, IHttpClientFactory httpFactory, CancellationToken ct) =>
        {
            if (!HasMinecraftAccess(http)) return Forbidden();
            var online = await GetOnlinePlayersAsync(cfg, httpFactory, ct);
            return Microsoft.AspNetCore.Http.Results.Ok(new { onlinePlayers = online, available = online.HasValue });
        });

        app.MapGet("/api/integrations/minecraft/chat/messages", async (HttpContext http, MinecraftDbContext db, int take, CancellationToken ct) =>
        {
            if (!HasMinecraftAccess(http)) return Forbidden();
            var limit = Math.Clamp(take <= 0 ? 60 : take, 1, 200);
            var rows = await db.ChatMessages.AsNoTracking()
                .OrderByDescending(x => x.CreatedAtUtc)
                .Take(limit)
                .OrderBy(x => x.CreatedAtUtc)
                .ToListAsync(ct);
            return Microsoft.AspNetCore.Http.Results.Ok(rows.Select(ToMinecraftChatDto));
        });

        app.MapPost("/api/integrations/minecraft/chat/messages", async (MinecraftChatRequest req, HttpContext http, IConfiguration cfg, MinecraftDbContext db, IHttpClientFactory httpFactory, IHubContext<MinecraftChatHub> hub, CancellationToken ct) =>
        {
            if (!HasMinecraftAccess(http)) return Forbidden();
            var uid = UserId(http, cfg);
            if (uid == null) return Unauthorized();
            var text = NormalizeMessage(req.Message ?? req.Text);
            if (string.IsNullOrWhiteSpace(text)) return Microsoft.AspNetCore.Http.Results.BadRequest(new { message = "Сообщение пустое." });

            var users = await TaskForge.Minecraft.Api.Services.Mapping.MinecraftApiMappingService.LoadUserSummariesAsync(new[] { uid.Value }, cfg, httpFactory, ct);
            users.TryGetValue(uid.Value, out var user);
            var msg = new MinecraftChatMessage
            {
                Id = Guid.NewGuid(),
                UserId = uid.Value,
                Source = TaskForgeRequestSecurity.HasAnyRole(http.User, "Admin") ? "SiteAdmin" : "SiteUser",
                AuthorName = TaskForge.Minecraft.Api.Services.Mapping.MinecraftApiMappingService.UserLabel(user),
                Message = text,
                CreatedAtUtc = DateTimeOffset.UtcNow
            };
            db.ChatMessages.Add(msg);
            await db.SaveChangesAsync(ct);
            await BroadcastAsync(hub, msg, ct);
            return Microsoft.AspNetCore.Http.Results.Ok(ToMinecraftChatDto(msg));
        });

        app.MapPost("/api/integrations/minecraft/chat/bridge/incoming", async (IncomingMinecraftChatRequest req, HttpContext http, IConfiguration cfg, MinecraftDbContext db, IHubContext<MinecraftChatHub> hub, CancellationToken ct) =>
        {
            if (!IsPluginAuthorized(http, cfg)) return Microsoft.AspNetCore.Http.Results.Unauthorized();
            var nick = NormalizeNick(req.Nick);
            var text = NormalizeMessage(req.Message);
            var source = NormalizeSource(req.Kind);
            if (string.IsNullOrWhiteSpace(nick) || string.IsNullOrWhiteSpace(text) || IsSuppressedMinecraftMessage(source, text)) return Microsoft.AspNetCore.Http.Results.Ok(new { ignored = true });

            var uuid = (req.Uuid ?? string.Empty).Trim();
            var linkedUserId = await db.Links.AsNoTracking()
                .Where(x => x.Confirmed && x.UnlinkedAtUtc == null && ((uuid.Length > 0 && x.PlayerUuid == uuid) || (x.PlayerName != null && x.PlayerName.ToLower() == nick.ToLower())))
                .Select(x => x.UserId)
                .FirstOrDefaultAsync(ct);
            var msg = new MinecraftChatMessage
            {
                Id = Guid.NewGuid(),
                UserId = linkedUserId,
                Source = source,
                AuthorName = nick,
                MinecraftNick = nick,
                MinecraftUuid = uuid.Length == 0 ? null : uuid,
                Message = text,
                CreatedAtUtc = DateTimeOffset.UtcNow
            };
            db.ChatMessages.Add(msg);
            await db.SaveChangesAsync(ct);
            await BroadcastAsync(hub, msg, ct);
            return Microsoft.AspNetCore.Http.Results.Ok(ToMinecraftChatDto(msg));
        });

        app.MapGet("/api/integrations/minecraft/chat/bridge/pull", async (DateTimeOffset? afterUtc, int take, HttpContext http, IConfiguration cfg, MinecraftDbContext db, CancellationToken ct) =>
        {
            if (!IsPluginAuthorized(http, cfg)) return Microsoft.AspNetCore.Http.Results.Unauthorized();
            var limit = Math.Clamp(take <= 0 ? 50 : take, 1, 200);
            var query = db.ChatMessages.AsNoTracking().Where(x => x.Source == "SiteUser" || x.Source == "SiteAdmin");
            if (afterUtc.HasValue) query = query.Where(x => x.CreatedAtUtc > afterUtc.Value);
            var rows = await query.OrderBy(x => x.CreatedAtUtc).Take(limit).ToListAsync(ct);
            return Microsoft.AspNetCore.Http.Results.Ok(rows.Select(ToMinecraftChatDto));
        });

        app.MapHub<MinecraftChatHub>("/hubs/minecraft-chat");

        return app;
    }
}
