using Microsoft.EntityFrameworkCore;
using TaskForge.Minecraft.Api.Data;

namespace TaskForge.Minecraft.Api.Endpoints;

internal static partial class MinecraftApiEndpoints
{
    private static WebApplication MapAccountIntelligenceInternalEndpoints(WebApplication app)
    {
        app.MapGet("/api/internal/account-intelligence/minecraft-snapshot", async (MinecraftDbContext db, CancellationToken ct) =>
        {
            var links = await db.Links.AsNoTracking()
                .Where(x => x.UserId.HasValue && x.Confirmed && x.UnlinkedAtUtc == null)
                .Select(x => new
                {
                    userId = x.UserId!.Value,
                    linkId = x.Id,
                    x.PlayerName,
                    x.PlayerUuid,
                    linkedAtUtc = x.ConfirmedAtUtc ?? x.CreatedAt,
                })
                .ToListAsync(ct);

            var items = links.GroupBy(x => x.userId).Select(group => new
            {
                userId = group.Key,
                links = group.OrderBy(x => x.linkedAtUtc).ToArray(),
                firstLinkedAtUtc = group.Min(x => x.linkedAtUtc),
                lastLinkedAtUtc = group.Max(x => x.linkedAtUtc),
            }).ToList();

            return Results.Ok(new { generatedAtUtc = DateTimeOffset.UtcNow, items });
        });

        return app;
    }
}
