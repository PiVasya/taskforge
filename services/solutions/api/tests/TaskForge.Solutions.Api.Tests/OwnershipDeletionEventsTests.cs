using TaskForge.Solutions.Api.Domain;
using TaskForge.Solutions.Api.Services.Results;
using Xunit;

namespace TaskForge.Solutions.Api.Tests;

public sealed class OwnershipDeletionEventsTests
{
    [Theory]
    [InlineData("sql", true)]
    [InlineData("SQL", true)]
    [InlineData("mysql", true)]
    [InlineData("sqlite", true)]
    [InlineData("csharp", false)]
    public void SubmissionDeletionUsesSameClassificationAsNormalEvent(string language, bool sql)
    {
        var row = new SolutionSubmission
        {
            AssignmentId = Guid.NewGuid(), UserId = Guid.NewGuid(),
            Language = language, Score = 35
        };
        var deleted = OwnershipDeletionEvents.ForSubmission(row);
        Assert.Equal(sql ? "sql" : "code", deleted.Kind);
        Assert.Equal("Deleted", deleted.Status);
        Assert.Equal(row.Id, deleted.ItemId);
        Assert.Equal(row.UserId, deleted.UserId);
        Assert.Equal(row.AssignmentId, deleted.AssignmentId);
    }

    [Fact]
    public void HistoricalSqlProfileWithoutSqlLanguageIsDeletedAsSql()
    {
        var row = new SolutionSubmission { SqlEngineProfileId = Guid.NewGuid() };
        Assert.Equal("sql", OwnershipDeletionEvents.ForSubmission(row).Kind);
    }

    [Fact]
    public void DeletedImageKeepsOriginalIdentity()
    {
        var row = new UserImageTaskSolution
        {
            AssignmentId = Guid.NewGuid(), UserId = Guid.NewGuid(),
            SimilarityPercent = 82
        };
        var deleted = OwnershipDeletionEvents.ForImage(row);
        Assert.Equal("image", deleted.Kind);
        Assert.Equal("Deleted", deleted.Status);
        Assert.Equal(row.Id, deleted.ItemId);
        Assert.Equal(row.UserId, deleted.UserId);
        Assert.Equal(row.AssignmentId, deleted.AssignmentId);
    }
}
