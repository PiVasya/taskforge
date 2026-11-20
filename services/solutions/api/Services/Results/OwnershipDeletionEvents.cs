using TaskForge.Realtime;
using TaskForge.Solutions.Api.Domain;

namespace TaskForge.Solutions.Api.Services.Results;

// A deletion event is committed in the same database transaction as the
// source-side delete. The relay acknowledges it only after Tasks deletes the
// corresponding owned snapshot. No new persistence table is required.
internal static class OwnershipDeletionEvents
{
    internal static string SubmissionKind(SolutionSubmission row) =>
        row.SqlSpecVersionId.HasValue || row.SqlEngineProfileId.HasValue ||
        string.Equals(row.Language, "sql", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(row.Language, "postgresql", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(row.Language, "mysql", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(row.Language, "sqlite", StringComparison.OrdinalIgnoreCase)
            ? "sql" : "code";

    internal static AdminSolutionFeedEntry ForSubmission(SolutionSubmission row) =>
        new()
        {
            Kind = SubmissionKind(row), ItemId = row.Id,
            UserId = row.UserId ?? Guid.Empty, AssignmentId = row.AssignmentId,
            Status = "Deleted", Score = row.Score
        };

    internal static AdminSolutionFeedEntry ForAccountDeletedSubmission(SolutionSubmission row)
    {
        var deleted = ForSubmission(row);
        deleted.UserId = Guid.Empty;
        return deleted;
    }

    internal static AdminSolutionFeedEntry ForAccountDeletedImage(UserImageTaskSolution row)
    {
        var deleted = ForImage(row);
        deleted.UserId = Guid.Empty;
        return deleted;
    }

    internal static AdminSolutionFeedEntry ForImage(UserImageTaskSolution row) =>
        new()
        {
            Kind = "image", ItemId = row.Id, UserId = row.UserId,
            AssignmentId = row.AssignmentId, Status = "Deleted",
            Score = row.SimilarityPercent
        };
}
