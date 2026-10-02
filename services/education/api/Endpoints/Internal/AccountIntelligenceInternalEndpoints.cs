using Microsoft.EntityFrameworkCore;
using TaskForge.Education.Api.Data;

namespace TaskForge.Education.Api.Endpoints;

internal static partial class EducationApiEndpoints
{
    private static WebApplication MapAccountIntelligenceInternalEndpoints(WebApplication app)
    {
        app.MapGet("/api/internal/account-intelligence/education-snapshot", async (EducationDbContext db, CancellationToken ct) =>
        {
            var groupRows = await db.Groups.AsNoTracking()
                .Select(x => new { groupId = x.Id, x.Name, x.CreatedAt })
                .ToListAsync(ct);
            var ownerRows = await db.GroupOwners.AsNoTracking()
                .Select(x => new { x.GroupId, x.UserId })
                .ToListAsync(ct);
            var ownersByGroup = ownerRows
                .GroupBy(x => x.GroupId)
                .ToDictionary(x => x.Key, x => x.Select(row => row.UserId).Distinct().ToArray());
            var groups = groupRows.Select(x => new
            {
                x.groupId,
                x.Name,
                ownerUserIds = ownersByGroup.GetValueOrDefault(x.groupId, Array.Empty<Guid>()),
                x.CreatedAt
            }).ToList();
            var memberships = await db.GroupMembers.AsNoTracking()
                .Select(x => new { x.UserId, x.GroupId, x.CreatedAt })
                .ToListAsync(ct);

            return Results.Ok(new
            {
                generatedAtUtc = DateTimeOffset.UtcNow,
                groups,
                memberships
            });
        });

        return app;
    }
}
