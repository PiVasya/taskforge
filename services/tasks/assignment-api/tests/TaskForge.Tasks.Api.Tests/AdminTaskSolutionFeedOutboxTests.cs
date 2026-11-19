using Microsoft.EntityFrameworkCore;
using TaskForge.Tasks.Api.Data;
using TaskForge.Tasks.Api.Domain;
using Xunit;

namespace TaskForge.Tasks.Api.Tests;

public sealed class AdminTaskSolutionFeedOutboxTests
{
    private static TasksDbContext Context() => new(
        new DbContextOptionsBuilder<TasksDbContext>()
            .UseNpgsql("Host=127.0.0.1;Database=not-connected;Username=unused")
            .Options);

    [Theory]
    [InlineData("test")]
    [InlineData("math")]
    public void ActiveAttemptsAreNotPublishedUntilSubmission(string kind)
    {
        using var db = Context();
        var attempt = new TaskAttempt
        {
            Kind = kind, UserId = Guid.NewGuid(), TaskAssignmentId = Guid.NewGuid()
        };
        db.Attempts.Add(attempt);
        db.CaptureAdminSolutionEvents();
        Assert.Empty(db.AdminSolutionFeedEvents.Local);
        db.ChangeTracker.AcceptAllChanges();

        attempt.SubmittedAt = DateTimeOffset.UtcNow;
        attempt.ScorePercent = 76;
        attempt.Passed = true;
        db.CaptureAdminSolutionEvents();
        db.CaptureAdminSolutionEvents(); // retry must not multiply the pending outbox entry
        var evt = Assert.Single(db.AdminSolutionFeedEvents.Local);
        Assert.Equal(kind, evt.Kind);
        Assert.Equal(attempt.Id, evt.ItemId);
        Assert.Equal("Accepted", evt.Status);
        Assert.Equal(76, evt.Score);
        Assert.Equal(attempt.TaskAssignmentId, evt.AssignmentId);
    }

    [Fact]
    public void UnchangedSubmittedAttemptDoesNotCreateDuplicateEvent()
    {
        using var db = Context();
        var attempt = new TaskAttempt
        {
            Kind = "test", UserId = Guid.NewGuid(), TaskAssignmentId = Guid.NewGuid(),
            SubmittedAt = DateTimeOffset.UtcNow
        };
        db.Attempts.Attach(attempt);
        db.CaptureAdminSolutionEvents();
        Assert.Empty(db.AdminSolutionFeedEvents.Local);
    }
}
