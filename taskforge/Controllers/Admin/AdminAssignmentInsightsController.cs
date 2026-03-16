using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using taskforge.Constants;
using taskforge.Data;

namespace taskforge.Controllers.Admin
{
    [ApiController]
    [Route("api/admin/assignments")]
    [Authorize(Roles = AppRoles.Admin)]
    public sealed class AdminAssignmentInsightsController : ControllerBase
    {
        private readonly ApplicationDbContext _db;

        public AdminAssignmentInsightsController(ApplicationDbContext db)
        {
            _db = db;
        }

        public sealed record AssignmentInsightSummaryDto(
            Guid AssignmentId,
            string Title,
            string Type,
            Guid CourseId,
            string CourseTitle,
            int Rating,
            int Difficulty,
            int UniqueUsers,
            int SuccessUsers,
            int CodeAttempts,
            int PassedCodeAttempts,
            int TestAttempts,
            int PassedTests,
            int ImageAttempts,
            int PassedImages,
            double? AvgReviewSeconds,
            IReadOnlyList<LanguageStatDto> Languages,
            IReadOnlyList<SolverStatDto> Solvers,
            IReadOnlyList<ActivityDto> RecentActivity,
            string? AvgReviewNote);

        public sealed record LanguageStatDto(string Language, int Count);
        public sealed record SolverStatDto(Guid UserId, string FullName, string Email, int TotalAttempts, int SuccessfulAttempts, DateTime FirstActivityAtUtc, DateTime LastActivityAtUtc);
        public sealed record ActivityDto(Guid UserId, string FullName, string Email, string SourceKind, string Status, string? Language, bool HasCode, string? FullCode, int? PassedCount, int? FailedCount, int? ScorePercent, double? SimilarityPercent, double? DurationSeconds, DateTime CreatedAtUtc);

        [HttpGet("{assignmentId:guid}/insights")]
        public async Task<IActionResult> Get(Guid assignmentId, CancellationToken ct = default)
        {
            var assignment = await _db.TaskAssignments.AsNoTracking()
                .Where(x => x.Id == assignmentId)
                .Select(x => new { x.Id, x.Title, x.Type, x.CourseId, CourseTitle = x.Course.Title, x.Rating, x.Difficulty })
                .FirstOrDefaultAsync(ct);
            if (assignment == null) return NotFound();

            var codeRows = await _db.UserTaskSolutions.AsNoTracking()
                .Where(x => x.TaskAssignmentId == assignmentId)
                .Select(x => new
                {
                    x.UserId,
                    FullName = (x.User.FirstName + " " + x.User.LastName).Trim(),
                    x.User.Email,
                    x.Language,
                    x.PassedAllTests,
                    x.PassedCount,
                    x.FailedCount,
                    x.SubmittedAt,
                    x.SubmittedCode
                })
                .ToListAsync(ct);

            var testRows = await _db.UserTaskTestAttempts.AsNoTracking()
                .Where(x => x.TaskAssignmentId == assignmentId)
                .Select(x => new
                {
                    x.UserId,
                    FullName = (x.User!.FirstName + " " + x.User!.LastName).Trim(),
                    Email = x.User!.Email,
                    x.Passed,
                    x.ScorePercent,
                    x.StartedAt,
                    x.SubmittedAt
                })
                .ToListAsync(ct);

            var imageRows = await _db.UserImageTaskSolutions.AsNoTracking()
                .Where(x => x.TaskAssignmentId == assignmentId)
                .Select(x => new
                {
                    x.UserId,
                    FullName = (x.User.FirstName + " " + x.User.LastName).Trim(),
                    x.User.Email,
                    x.Language,
                    x.Passed,
                    x.SimilarityPercent,
                    x.CreatedAtUtc,
                    x.SubmittedCode,
                    x.Kind,
                    x.IsTrial
                })
                .ToListAsync(ct);

            var languages = codeRows.Where(x => !string.IsNullOrWhiteSpace(x.Language))
                .Select(x => x.Language!.Trim())
                .Concat(imageRows.Where(x => !string.IsNullOrWhiteSpace(x.Language)).Select(x => x.Language!.Trim()))
                .GroupBy(x => x)
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key)
                .Select(g => new LanguageStatDto(g.Key, g.Count()))
                .ToList();

            var activity = new List<ActivityDto>();
            activity.AddRange(codeRows.Select(x => new ActivityDto(
                x.UserId,
                x.FullName,
                x.Email,
                "code",
                x.PassedAllTests ? "passed" : "failed",
                x.Language,
                !string.IsNullOrWhiteSpace(x.SubmittedCode),
                x.SubmittedCode,
                x.PassedCount,
                x.FailedCount,
                null,
                null,
                null,
                x.SubmittedAt
            )));
            activity.AddRange(testRows.Select(x => new ActivityDto(
                x.UserId,
                x.FullName,
                x.Email,
                "test",
                x.Passed ? "passed" : "failed",
                null,
                false,
                null,
                null,
                null,
                x.ScorePercent,
                null,
                x.SubmittedAt != null ? Math.Round((x.SubmittedAt.Value - x.StartedAt).TotalSeconds, 2) : null,
                x.SubmittedAt ?? x.StartedAt
            )));
            activity.AddRange(imageRows.Select(x => new ActivityDto(
                x.UserId,
                x.FullName,
                x.Email,
                x.IsTrial ? "image-trial" : "image",
                x.Passed == true ? "passed" : x.Passed == false ? "failed" : "pending",
                x.Language,
                !string.IsNullOrWhiteSpace(x.SubmittedCode),
                x.SubmittedCode,
                null,
                null,
                null,
                x.SimilarityPercent,
                null,
                x.CreatedAtUtc
            )));
            activity = activity.OrderByDescending(x => x.CreatedAtUtc).Take(80).ToList();

            var solverStats = activity
                .GroupBy(x => new { x.UserId, x.FullName, x.Email })
                .Select(g => new SolverStatDto(
                    g.Key.UserId,
                    g.Key.FullName,
                    g.Key.Email,
                    g.Count(),
                    g.Count(x => x.Status == "passed"),
                    g.Min(x => x.CreatedAtUtc),
                    g.Max(x => x.CreatedAtUtc)
                ))
                .OrderByDescending(x => x.SuccessfulAttempts)
                .ThenByDescending(x => x.TotalAttempts)
                .ThenBy(x => x.FullName)
                .Take(60)
                .ToList();

            var uniqueUsers = activity.Select(x => x.UserId).Distinct().Count();
            var successUsers = activity.Where(x => x.Status == "passed").Select(x => x.UserId).Distinct().Count();
            var durations = testRows.Where(x => x.SubmittedAt != null && x.SubmittedAt >= x.StartedAt)
                .Select(x => (x.SubmittedAt!.Value - x.StartedAt).TotalSeconds)
                .Where(x => x >= 0)
                .ToList();
            double? avgReviewSeconds = durations.Count > 0 ? Math.Round(durations.Average(), 2) : null;
            var avgReviewNote = avgReviewSeconds == null ? "Для code/image точное время выполнения в текущей модели не хранится. Показывается среднее время только по test-попыткам." : null;

            return Ok(new AssignmentInsightSummaryDto(
                assignment.Id,
                assignment.Title,
                assignment.Type,
                assignment.CourseId,
                assignment.CourseTitle,
                assignment.Rating,
                assignment.Difficulty,
                uniqueUsers,
                successUsers,
                codeRows.Count,
                codeRows.Count(x => x.PassedAllTests),
                testRows.Count,
                testRows.Count(x => x.Passed),
                imageRows.Count,
                imageRows.Count(x => x.Passed == true),
                avgReviewSeconds,
                languages,
                solverStats,
                activity,
                avgReviewNote
            ));
        }

    }
}
