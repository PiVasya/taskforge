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
    private static WebApplication MapAdminEndpoints(WebApplication app)
    {
        app.MapGet("/api/admin/minecraft-links", async (MinecraftDbContext db, IConfiguration cfg, IHttpClientFactory httpFactory, string? query, CancellationToken ct) =>
        {
            var links = await db.Links.AsNoTracking().Where(x => x.Confirmed).OrderByDescending(x => x.CreatedAt).Take(1000).ToListAsync(ct);
            var users = await LoadUserSummariesAsync(links.Select(x => x.UserId).Where(x => x.HasValue).Select(x => x!.Value), cfg, httpFactory, ct);
            var grouped = links.GroupBy(x => x.UserId ?? Guid.Empty).Select(g =>
            {
                var latest = g.OrderByDescending(x => x.CreatedAt).First();
                var user = latest.UserId.HasValue ? users.GetValueOrDefault(latest.UserId.Value) : null;
                return new
                {
                    id = latest.Id,
                    userId = latest.UserId,
                    login = user?.Login,
                    fullName = UserLabel(user),
                    displayName = UserLabel(user),
                    email = user?.Email ?? user?.MaskedEmail,
                    minecraftNick = latest.PlayerName,
                    nick = latest.PlayerName,
                    minecraftUuid = latest.PlayerUuid,
                    uuid = latest.PlayerUuid,
                    linkedAtUtc = latest.CreatedAt,
                    linkCount = g.Count(),
                    totalScore = 0,
                    effectiveScore = 0,
                    totalPenalty = 0,
                    weeklyJoinEvents = 0,
                    featureRoles = Array.Empty<string>(),
                    lastPenaltyAtUtc = (DateTimeOffset?)null
                };
            }).ToList();

            var search = (query ?? string.Empty).Trim().ToLowerInvariant();
            if (!string.IsNullOrWhiteSpace(search))
            {
                grouped = grouped.Where(x => string.Join(' ', x.fullName, x.email, x.minecraftNick, x.minecraftUuid).ToLowerInvariant().Contains(search)).ToList();
            }
            return Results.Ok(grouped);
        });

        return app;
    }
}
