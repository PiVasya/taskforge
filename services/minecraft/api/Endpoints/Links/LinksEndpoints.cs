using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using TaskForge.Minecraft.Api.Data;
using TaskForge.Minecraft.Api.Domain;

using TaskForge.Minecraft.Api.Contracts;
using TaskForge.Minecraft.Api.Hubs;
using static TaskForge.Minecraft.Api.Services.Common.MinecraftApiCommonService;
using static TaskForge.Minecraft.Api.Services.Mapping.MinecraftApiMappingService;
using static TaskForge.Minecraft.Api.Services.Serialization.MinecraftApiSerializationService;

namespace TaskForge.Minecraft.Api.Endpoints;

internal static partial class MinecraftApiEndpoints
{
    private static WebApplication MapLinksEndpoints(WebApplication app)
    {
        app.MapGet("/api/integrations/minecraft/status", async (HttpContext http, IConfiguration cfg, MinecraftDbContext db, CancellationToken ct) =>
        {
            var uid = TaskForgeRequestSecurity.UserId(http, cfg);
            if (uid == null) return Unauthorized();
            var confirmed = await db.Links.AsNoTracking().Where(x => x.UserId == uid && x.Confirmed).OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync(ct);
            var count = await db.Links.AsNoTracking().CountAsync(x => x.UserId == uid, ct);
            return Microsoft.AspNetCore.Http.Results.Ok(new
            {
                linked = confirmed != null,
                playerName = confirmed?.PlayerName,
                nick = confirmed?.PlayerName,
                minecraftNick = confirmed?.PlayerName,
                playerUuid = confirmed?.PlayerUuid,
                uuid = confirmed?.PlayerUuid,
                minecraftUuid = confirmed?.PlayerUuid,
                linkedAtUtc = confirmed?.CreatedAt,
                linkCount = count
            });
        });

        app.MapPost("/api/integrations/minecraft/request", async (HttpContext http, IConfiguration cfg, MinecraftDbContext db, CancellationToken ct) =>
        {
            var uid = TaskForgeRequestSecurity.UserId(http, cfg);
            if (uid == null) return Unauthorized();
            var active = await db.Links.Where(x => x.UserId == uid && !x.Confirmed).ToListAsync(ct);
            if (active.Count > 0) db.Links.RemoveRange(active);
            var link = new MinecraftLink { UserId = uid, Code = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(), CreatedAt = DateTimeOffset.UtcNow };
            db.Links.Add(link);
            await db.SaveChangesAsync(ct);
            return Microsoft.AspNetCore.Http.Results.Ok(new { code = link.Code, expiresInSeconds = 600 });
        });

        app.MapPost("/api/integrations/minecraft/confirm", async (MinecraftConfirmRequest req, HttpContext http, IConfiguration cfg, MinecraftDbContext db, CancellationToken ct) =>
        {
            var uid = TaskForgeRequestSecurity.UserId(http, cfg);
            if (uid == null) return Unauthorized();
            var code = (req.Code ?? string.Empty).Trim().ToUpperInvariant();
            var link = await db.Links.Where(x => x.UserId == uid && x.Code == code).OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync(ct);
            if (link == null) return Microsoft.AspNetCore.Http.Results.NotFound(new { message = "Код привязки не найден для текущего пользователя.", code = "MINECRAFT_LINK_CODE_NOT_FOUND" });
            link.Confirmed = true;
            link.PlayerName = string.IsNullOrWhiteSpace(req.PlayerName) ? link.PlayerName : req.PlayerName.Trim();
            link.PlayerUuid = string.IsNullOrWhiteSpace(req.PlayerUuid) ? link.PlayerUuid : req.PlayerUuid.Trim();
            await db.SaveChangesAsync(ct);
            return Microsoft.AspNetCore.Http.Results.Ok(new { linked = true, playerName = link.PlayerName, nick = link.PlayerName, uuid = link.PlayerUuid, minecraftUuid = link.PlayerUuid });
        });

        app.MapDelete("/api/integrations/minecraft/unlink", async (HttpContext http, IConfiguration cfg, MinecraftDbContext db, CancellationToken ct) =>
        {
            var uid = TaskForgeRequestSecurity.UserId(http, cfg);
            if (uid == null) return Unauthorized();
            var links = await db.Links.Where(x => x.UserId == uid).ToListAsync(ct);
            db.Links.RemoveRange(links);
            await db.SaveChangesAsync(ct);
            return Microsoft.AspNetCore.Http.Results.Ok(new { linked = false });
        });

        return app;
    }
}
