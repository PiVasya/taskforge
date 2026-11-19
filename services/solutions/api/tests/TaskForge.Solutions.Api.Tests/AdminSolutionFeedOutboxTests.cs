using Microsoft.EntityFrameworkCore;
using TaskForge.Realtime;
using TaskForge.Solutions.Api.Data;
using TaskForge.Solutions.Api.Domain;
using Xunit;

namespace TaskForge.Solutions.Api.Tests;

public sealed class AdminSolutionFeedOutboxTests
{
    private static SolutionsDbContext Context() => new(
        new DbContextOptionsBuilder<SolutionsDbContext>()
            .UseNpgsql("Host=127.0.0.1;Database=not-connected;Username=unused")
            .Options);

    [Fact]
    public void CodeSubmissionAndVerdictCreateTwoDistinctDurableEntries()
    {
        using var db = Context();
        var user = Guid.NewGuid();
        var solution = new SolutionSubmission { AssignmentId = Guid.NewGuid(), UserId = user, Language = "csharp" };
        db.Submissions.Add(solution);
        db.CaptureAdminSolutionEvents();
        db.CaptureAdminSolutionEvents(); // transient retry before DB COMMIT
        var created = Assert.Single(db.AdminSolutionFeedEvents.Local);
        Assert.Equal("code", created.Kind);
        Assert.Equal(solution.Id, created.ItemId);
        Assert.Equal(user, created.UserId);
        Assert.Equal("Preparing", created.Status);

        // Model what EF does after the first successful SaveChanges.
        db.ChangeTracker.AcceptAllChanges();
        solution.Status = "Accepted";
        solution.Score = 100;
        db.CaptureAdminSolutionEvents();
        var verdict = Assert.Single(db.AdminSolutionFeedEvents.Local.Where(x => x.Id != created.Id));
        Assert.Equal("Accepted", verdict.Status);
        Assert.Equal(100, verdict.Score);
        Assert.Equal(created.ItemId, verdict.ItemId);
    }

    [Fact]
    public void SqlAndImageEntriesStayInTheSolutionsOwner()
    {
        using var db = Context();
        db.Submissions.Add(new SolutionSubmission
        {
            AssignmentId = Guid.NewGuid(), UserId = Guid.NewGuid(), SqlSpecVersionId = Guid.NewGuid(),
        });
        db.ImageSolutions.Add(new UserImageTaskSolution
        {
            AssignmentId = Guid.NewGuid(), UserId = Guid.NewGuid(), Passed = true,
            SimilarityPercent = 93
        });
        db.CaptureAdminSolutionEvents();
        var rows = db.AdminSolutionFeedEvents.Local.ToList();
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, x => x.Kind == "sql");
        Assert.Contains(rows, x => x.Kind == "image" && x.Score == 93);
    }

    [Fact]
    public void CursorCanBeParsedAfterSseReconnection()
    {
        var row = new AdminSolutionFeedEntry { Kind = "code" };
        var cursor = AdminSolutionFeedSse.Cursor(row);
        Assert.Equal(row.OccurredAtUtc.ToUnixTimeMilliseconds(),
            AdminSolutionFeedSse.ParseCursor(cursor)?.ToUnixTimeMilliseconds());
        Assert.Null(AdminSolutionFeedSse.ParseCursor("corrupt"));
        Assert.Null(AdminSolutionFeedSse.ParseCursor("0:invalid-uuid"));
    }
}
