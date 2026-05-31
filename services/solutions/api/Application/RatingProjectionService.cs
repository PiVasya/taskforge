using Microsoft.EntityFrameworkCore;
using TaskForge.Solutions.Api.Data;
using TaskForge.Solutions.Api.Domain;

namespace TaskForge.Solutions.Api.Application;

public sealed class RatingProjectionService(SolutionsDbContext db)
{
    public async Task ApplyVerdictAsync(SolutionVerdictChangedEvent evt, CancellationToken ct = default)
    {
        var rating = await db.UserRatings.FirstOrDefaultAsync(x => x.UserId == evt.UserId, ct);
        if (rating is null)
        {
            rating = new UserRating { UserId = evt.UserId };
            db.UserRatings.Add(rating);
        }

        rating.AttemptsCount++;
        rating.TotalScore += evt.ScoreDelta;
        if (evt.IsAccepted)
        {
            rating.AcceptedCount++;
            rating.SolvedCount++;
            rating.LastAcceptedAt = evt.OccurredAt;
        }
        else
        {
            rating.RejectedCount++;
        }

        rating.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public Task<List<LeaderboardEntry>> ReadLeaderboardAsync(string scope = "global", int take = 100, CancellationToken ct = default)
    {
        return db.LeaderboardEntries
            .AsNoTracking()
            .Where(x => x.Scope == scope)
            .OrderBy(x => x.Rank)
            .Take(take)
            .ToListAsync(ct);
    }
}
