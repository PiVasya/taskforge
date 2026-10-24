using TaskForge.Solutions.Api.Contracts;
using TaskForge.Solutions.Api.Services.Results;
using Xunit;

namespace TaskForge.Solutions.Api.Tests;

public sealed class DeletedAssignmentRatingTests
{
    [Fact]
    public void MissingAssignmentHasNoResidualScore()
    {
        Assert.Equal(0, SolutionsApiResultsService.MetadataRating(new Dictionary<Guid, AssignmentMetadata>(), Guid.NewGuid()));
    }

    [Fact]
    public void ExistingAssignmentRetainsItsConfiguredScore()
    {
        var id = Guid.NewGuid();
        var rows = new Dictionary<Guid, AssignmentMetadata>
        {
            [id] = new(id, Guid.NewGuid(), "Task", "Course", 1_000_000)
        };
        Assert.Equal(1_000_000, SolutionsApiResultsService.MetadataRating(rows, id));
    }
}
