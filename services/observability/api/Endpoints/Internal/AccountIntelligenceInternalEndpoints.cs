using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using TaskForge.Observability.Api.Data;

namespace TaskForge.Observability.Api.Endpoints;

internal static partial class ObservabilityApiEndpoints
{
    private static WebApplication MapAccountIntelligenceInternalEndpoints(WebApplication app)
    {
        app.MapGet("/api/internal/account-intelligence/observability-snapshot", async (
            ObservabilityDbContext db,
            int days = 365,
            CancellationToken ct = default) =>
        {
            days = Math.Clamp(days, 30, 1095);
            var since = DateTimeOffset.UtcNow.AddDays(-days);
            const int sampleLimit = 200_000;
            var rows = await db.PageViews.AsNoTracking()
                .Where(x => x.UserId.HasValue && x.CreatedAt >= since)
                .OrderByDescending(x => x.CreatedAt)
                .Take(sampleLimit)
                .Select(x => new
                {
                    UserId = x.UserId!.Value,
                    x.CreatedAt,
                    x.Path,
                    x.Action,
                    x.StatusCode,
                    x.UserAgent,
                    x.ClientIpHash,
                    x.ClientIpPrefix,
                })
                .ToListAsync(ct);

            var items = rows.GroupBy(x => x.UserId).Select(group =>
            {
                var successful = group.Count(x => !x.StatusCode.HasValue || x.StatusCode.Value < 400);
                return new
                {
                    userId = group.Key,
                    pageViews = group.Count(),
                    successfulActions = successful,
                    errorActions = group.Count() - successful,
                    activeDays = group.Select(x => x.CreatedAt.UtcDateTime.Date).Distinct().Count(),
                    lastActivityAt = group.Max(x => x.CreatedAt),
                    distinctPaths = group.Select(x => x.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                    ipHashes = group.Select(x => x.ClientIpHash).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).Take(128).ToArray(),
                    ipPrefixes = group.Select(x => x.ClientIpPrefix).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).Take(64).ToArray(),
                    userAgentHashes = group.Select(x => HashValue(x.UserAgent)).Where(x => x != null).Distinct(StringComparer.OrdinalIgnoreCase).Take(128).ToArray(),
                };
            }).ToList();

            return Results.Ok(new
            {
                generatedAtUtc = DateTimeOffset.UtcNow,
                periodDays = days,
                sampleLimit,
                sampledRows = rows.Count,
                items
            });
        });

        return app;
    }

    private static string? HashValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.Trim()))).ToLowerInvariant();
    }
}
