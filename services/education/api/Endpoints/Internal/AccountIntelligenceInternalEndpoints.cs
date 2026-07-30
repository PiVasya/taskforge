using Microsoft.EntityFrameworkCore;
using TaskForge.Education.Api.Data;

namespace TaskForge.Education.Api.Endpoints;

internal static partial class EducationApiEndpoints
{
    private static WebApplication MapAccountIntelligenceInternalEndpoints(WebApplication app)
    {
        app.MapGet("/api/internal/account-intelligence/education-snapshot", async (EducationDbContext db, CancellationToken ct) =>
        {
            var groups = await db.Groups.AsNoTracking()
                .Select(x => new { groupId = x.Id, x.Name, x.Code, x.IsActive, x.CreatedAt })
                .ToListAsync(ct);
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
