using TaskForge.Tasks.Api.Domain;
using TaskForge.Tasks.Api.Services.Results;
using Xunit;

namespace TaskForge.Tasks.Api.Tests;

public sealed class BulkDeleteAttemptsScopeTests
{
    [Theory]
    [InlineData("test")]
    [InlineData("math")]
    public void SubmittedAttemptsForUser_OnlyTargetsSelectedUsersSubmittedKind(string kind)
    {
        var selectedUser = Guid.NewGuid();
        var otherUser = Guid.NewGuid();
        var submitted = DateTimeOffset.UtcNow;
        var matching = new TaskAttempt { UserId = selectedUser, Kind = kind, SubmittedAt = submitted };
        var otherKind = new TaskAttempt { UserId = selectedUser, Kind = kind == "test" ? "math" : "test", SubmittedAt = submitted };
        var otherPerson = new TaskAttempt { UserId = otherUser, Kind = kind, SubmittedAt = submitted };
        var unfinished = new TaskAttempt { UserId = selectedUser, Kind = kind, SubmittedAt = null };
        var source = new[] { matching, otherKind, otherPerson, unfinished }.AsQueryable();

        var matches = AssignmentApiResultsService.SubmittedAttemptsForUser(source, selectedUser, kind).ToArray();

        Assert.Equal(new[] { matching.Id }, matches.Select(x => x.Id));
    }
}
