using TaskForge.Solutions.Api.Domain;
using TaskForge.Solutions.Api.Endpoints;
using Xunit;

namespace TaskForge.Solutions.Api.Tests;

public sealed class BulkDeleteImageSolutionsScopeTests
{
    [Fact]
    public void UserImageSolutionsForDeletion_OnlyTargetsSelectedUser()
    {
        var selectedUser = Guid.NewGuid();
        var otherUser = Guid.NewGuid();
        var selectedFirst = new UserImageTaskSolution { UserId = selectedUser };
        var selectedSecond = new UserImageTaskSolution { UserId = selectedUser };
        var unrelated = new UserImageTaskSolution { UserId = otherUser };
        var solutions = new[] { selectedFirst, unrelated, selectedSecond }.AsQueryable();

        var result = SolutionsApiEndpoints.UserImageSolutionsForDeletion(solutions, selectedUser).ToArray();

        Assert.Equal(new[] { selectedFirst.Id, selectedSecond.Id }, result.Select(x => x.Id));
    }
}
