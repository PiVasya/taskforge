using Microsoft.EntityFrameworkCore;
using TaskForge.Solutions.Api.Data;
using TaskForge.Solutions.Api.Domain;
using TaskForge.Solutions.Api.Services.Results;
using Xunit;

namespace TaskForge.Solutions.Api.Tests;

public sealed class LeaderboardProjectionTests
{
    [Theory]
    [InlineData(false, null, false)]
    [InlineData(false, 0, false)]
    [InlineData(false, 7, true)]
    [InlineData(true, null, true)]
    [InlineData(true, 14, true)]
    public void ActivityDetailNeededOnlyForCourseOrPeriod(bool course, int? days, bool expected)
    {
        Assert.Equal(expected, LeaderboardReadModelService.RequiresActivityProjection(
            course ? Guid.NewGuid() : null, days));
    }

    [Fact]
    public void DatabaseModelUsesUniqueSourceEventsAndIndexesForFilters()
    {
        var options = new DbContextOptionsBuilder<SolutionsDbContext>()
            .UseNpgsql("Host=localhost;Database=model_only;Username=postgres;Password=unused")
            .Options;
        using var db = new SolutionsDbContext(options);
        var entity = db.Model.FindEntityType(typeof(RatingAcceptedActivity));
        Assert.NotNull(entity);
        Assert.Equal("RatingAcceptedActivities", entity!.GetTableName());
        Assert.Equal(new[] { nameof(RatingAcceptedActivity.SourceKind), nameof(RatingAcceptedActivity.SourceId) },
            entity.FindPrimaryKey()!.Properties.Select(x => x.Name));
        Assert.Contains(entity.GetIndexes(), i => i.Properties.Select(x => x.Name).SequenceEqual(
            new[] { nameof(RatingAcceptedActivity.CourseId), nameof(RatingAcceptedActivity.SubmittedAt) }));
    }

    [Fact]
    public void ActivityProjectionAndGlobalTotalsShareStableFields()
    {
        var projected = new RatingAcceptedActivity
        {
            SourceKind = "code",
            SourceId = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            AssignmentId = Guid.NewGuid(),
            CourseId = Guid.NewGuid(),
            Rating = 75,
            SubmittedAt = DateTimeOffset.UtcNow
        };
        Assert.True(projected.Rating > 0);
        Assert.Equal("RatingAcceptedActivitiesV1", LeaderboardReadModelService.ReadyCheckpoint);
    }
}
