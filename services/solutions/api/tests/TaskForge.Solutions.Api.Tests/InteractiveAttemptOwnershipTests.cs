using Microsoft.EntityFrameworkCore;
using TaskForge.Solutions.Api.Data;
using TaskForge.Solutions.Api.Domain;
using TaskForge.Solutions.Api.Endpoints;
using TaskForge.Realtime;
using Xunit;

namespace TaskForge.Solutions.Api.Tests;

public sealed class SolutionsAttemptOwnershipTests
{
    private static SolutionsDbContext Context() => new(
        new DbContextOptionsBuilder<SolutionsDbContext>()
            .UseNpgsql("Host=127.0.0.1;Database=not-connected;Username=unused").Options);

    [Fact]
    public void AttemptTimeLimitsPreserveNullableSlotsAndDoNotRepeatTheLastSlot()
    {
        int?[] limits = [null, 600, 0, -1];
        Assert.Null(SolutionsApiEndpoints.SelectAttemptTimeLimit(limits, 0));
        Assert.Equal<int?>(600, SolutionsApiEndpoints.SelectAttemptTimeLimit(limits, 1));
        Assert.Null(SolutionsApiEndpoints.SelectAttemptTimeLimit(limits, 2));
        Assert.Null(SolutionsApiEndpoints.SelectAttemptTimeLimit(limits, 3));
        Assert.Null(SolutionsApiEndpoints.SelectAttemptTimeLimit(limits, 4));
        Assert.Null(SolutionsApiEndpoints.SelectAttemptTimeLimit(null, 0));
        Assert.Null(SolutionsApiEndpoints.SelectAttemptTimeLimit(limits, -1));
    }

    [Fact]
    public void SolutionsOwnsInteractiveAttemptsAndTheirJsonbColumns()
    {
        using var db = Context();
        var entity = db.Model.FindEntityType(typeof(TaskAttempt));
        Assert.NotNull(entity);
        Assert.Equal("TaskAttempts", entity!.GetTableName());
        Assert.Equal("jsonb", entity.FindProperty(nameof(TaskAttempt.AnswersJson))?.GetColumnType());
        Assert.Equal("jsonb", entity.FindProperty(nameof(TaskAttempt.ReviewJson))?.GetColumnType());
        Assert.Contains(entity.GetIndexes(), index => index.IsUnique &&
            index.Properties.Select(x => x.Name).SequenceEqual(new[]
            {
                nameof(TaskAttempt.UserId), nameof(TaskAttempt.Kind), nameof(TaskAttempt.TaskAssignmentId)
            }));
    }

    [Theory]
    [InlineData("test")]
    [InlineData("math")]
    public void AcceptedInteractiveAttemptIsPublishedOnlyAfterSubmission(string kind)
    {
        using var db = Context();
        var attempt = new TaskAttempt { Kind = kind, UserId = Guid.NewGuid(), TaskAssignmentId = Guid.NewGuid() };
        db.TaskAttempts.Add(attempt);
        db.CaptureInteractiveAttemptEvents();
        Assert.Empty(db.AdminSolutionFeedEvents.Local);
        db.ChangeTracker.AcceptAllChanges();
        attempt.SubmittedAt = DateTimeOffset.UtcNow;
        attempt.Passed = true;
        attempt.ScorePercent = 100;
        db.CaptureInteractiveAttemptEvents();
        db.CaptureInteractiveAttemptEvents();
        var evt = Assert.Single(db.AdminSolutionFeedEvents.Local);
        Assert.Equal("Accepted", evt.Status);
        Assert.Equal(kind, evt.Kind);
        Assert.Equal(attempt.Id, evt.ItemId);
    }

    [Fact]
    public void DeletingASubmittedAttemptProducesADeletedFeedEvent()
    {
        using var db = Context();
        var attempt = new TaskAttempt
        {
            UserId = Guid.NewGuid(), TaskAssignmentId = Guid.NewGuid(),
            SubmittedAt = DateTimeOffset.UtcNow, Passed = true
        };
        db.TaskAttempts.Attach(attempt);
        db.TaskAttempts.Remove(attempt);
        db.CaptureInteractiveAttemptEvents();
        db.CaptureInteractiveAttemptEvents();
        var evt = Assert.Single(db.AdminSolutionFeedEvents.Local);
        Assert.Equal("Deleted", evt.Status);
        Assert.Null(evt.Score);
    }
}
