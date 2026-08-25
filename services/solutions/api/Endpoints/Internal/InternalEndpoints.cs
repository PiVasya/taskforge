using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using TaskForge.Solutions.Api.Data;
using TaskForge.Solutions.Api.Domain;

using TaskForge.Solutions.Api.Contracts;
using static TaskForge.Solutions.Api.Services.Access.SolutionsApiAccessService;
using static TaskForge.Solutions.Api.Services.Common.SolutionsApiCommonService;
using static TaskForge.Solutions.Api.Services.Image.SolutionsApiImageService;
using static TaskForge.Solutions.Api.Services.Mapping.SolutionsApiMappingService;
using static TaskForge.Solutions.Api.Services.Results.SolutionsApiResultsService;
using static TaskForge.Solutions.Api.Services.Serialization.SolutionsApiSerializationService;
using static TaskForge.Solutions.Api.Services.Testing.SolutionsApiTestingService;

namespace TaskForge.Solutions.Api.Endpoints;

internal static partial class SolutionsApiEndpoints
{
    private static WebApplication MapInternalEndpoints(WebApplication app)
    {
        app.MapPost("/api/internal/quotas/consume", async (QuotaMutationRequest request, HttpContext http, IConfiguration cfg, SolutionsDbContext db, CancellationToken ct) =>
        {
            if (request.UserId == Guid.Empty)
            {
                return Problem(400, "QUOTA_USER_REQUIRED", "quotas.consume", "Не передан пользователь для списания энергии.");
            }

            var bucket = string.Equals(request.Bucket, "top", StringComparison.OrdinalIgnoreCase) ? "top" : "tasks";
            var policy = QuotaPolicy(cfg, bucket);
            var result = await ConsumeQuotaAsync(db, request.UserId, bucket, policy.Capacity, policy.Interval, request.Amount, ct);
            WriteQuotaHeaders(http.Response, result.quota);
            if (!result.consumed) return QuotaExceeded(result.quota);
            return Microsoft.AspNetCore.Http.Results.Ok(new { ok = true, consumed = true, quota = result.quota, request.Reason });
        });

        app.MapPost("/api/internal/quotas/refund", async (QuotaMutationRequest request, HttpContext http, IConfiguration cfg, SolutionsDbContext db, CancellationToken ct) =>
        {
            if (request.UserId == Guid.Empty)
            {
                return Problem(400, "QUOTA_USER_REQUIRED", "quotas.refund", "Не передан пользователь для возврата энергии.");
            }

            var bucket = string.Equals(request.Bucket, "top", StringComparison.OrdinalIgnoreCase) ? "top" : "tasks";
            var policy = QuotaPolicy(cfg, bucket);
            var quota = await RefundQuotaAsync(db, request.UserId, bucket, policy.Capacity, policy.Interval, request.Amount, ct);
            WriteQuotaHeaders(http.Response, quota);
            return Microsoft.AspNetCore.Http.Results.Ok(new { ok = true, refunded = true, quota, request.Reason });
        });

        app.MapPost("/api/internal/solutions/submissions/{submissionId:guid}/verdict", async (Guid submissionId, SolutionVerdictRequest request, SolutionsDbContext db, CancellationToken ct) =>
        {
            var sub = await db.Submissions.FirstOrDefaultAsync(x => x.Id == submissionId, ct);
            if (sub == null)
            {
                TaskForgeDebugTrace.Map("VERDICT_MISS", ("submission", submissionId));
                return Microsoft.AspNetCore.Http.Results.NotFound(new { message = "Решение не найдено.", code = "SOLUTION_NOT_FOUND" });
            }

            var previous = sub.Status;
            var incomingVerdict = CleanVerdict(request.Verdict);
            TaskForgeDebugTrace.Map("VERDICT_RECEIVED",
                ("submission", submissionId),
                ("user", sub.UserId),
                ("assignment", sub.AssignmentId),
                ("previousVerdict", previous),
                ("incomingVerdict", incomingVerdict),
                ("previousScore", sub.Score),
                ("incomingScore", request.Score));
            if (!ShouldApplyIncomingVerdict(previous, incomingVerdict))
            {
                TaskForgeDebugTrace.Map("VERDICT_IGNORED",
                    ("submission", submissionId),
                    ("user", sub.UserId),
                    ("assignment", sub.AssignmentId),
                    ("previousVerdict", previous),
                    ("incomingVerdict", incomingVerdict));
                return Microsoft.AspNetCore.Http.Results.Ok(ToDto(sub, includeSensitiveResult: true));
            }

            var previousScore = sub.Score;
            var isRatingRelevantBefore = AffectsRatingStatus(previous);
            sub.Status = incomingVerdict;
            sub.Score = System.Math.Clamp(request.Score, 0, 100);
            sub.ResultJson = request.Result.HasValue
                ? request.Result.Value.GetRawText()
                : JsonSerializer.Serialize(new { verdict = sub.Status, score = sub.Score, message = request.Message }, JsonOptions());

            var isRatingRelevantAfter = AffectsRatingStatus(sub.Status);
            if (sub.UserId.HasValue && (isRatingRelevantBefore || isRatingRelevantAfter) &&
                (!string.Equals(previous, sub.Status, StringComparison.OrdinalIgnoreCase) || previousScore != sub.Score))
            {
                await MarkRatingDirtyAsync(db, sub.UserId.Value, "code-verdict", sub.AssignmentId, ct);
            }

            await db.SaveChangesAsync(ct);
            TaskForgeDebugTrace.Map("VERDICT_SAVED",
                ("submission", submissionId),
                ("user", sub.UserId),
                ("assignment", sub.AssignmentId),
                ("verdict", sub.Status),
                ("score", sub.Score),
                ("accepted", string.Equals(sub.Status, "Accepted", StringComparison.OrdinalIgnoreCase)));
            return Microsoft.AspNetCore.Http.Results.Ok(ToDto(sub, includeSensitiveResult: true));
        });


        app.MapGet("/api/internal/assignments/{assignmentId:guid}/attempts-summary", async (Guid assignmentId, SolutionsDbContext db, CancellationToken ct) =>
        {
            static bool IsAccepted(string? status) => string.Equals(status, "Accepted", StringComparison.OrdinalIgnoreCase);
            static string? HashCodeText(string? code)
            {
                if (string.IsNullOrEmpty(code)) return null;
                var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(code));
                return Convert.ToHexString(bytes).ToLowerInvariant();
            }
            static string? SampleCode(string? code) => string.IsNullOrEmpty(code) ? null : (code.Length <= 1200 ? code : code[..1200]);

            var codeQuery = db.Submissions.AsNoTracking().Where(x => x.AssignmentId == assignmentId);
            var imageQuery = db.ImageSolutions.AsNoTracking().Where(x => x.AssignmentId == assignmentId);
            var codeCount = await codeQuery.CountAsync(ct);
            var passedCodeCount = await codeQuery.CountAsync(x => x.Status != null && x.Status.ToLower() == "accepted", ct);
            var imageCount = await imageQuery.CountAsync(ct);
            var passedImageCount = await imageQuery.CountAsync(x => x.Passed, ct);

            var codeRows = await codeQuery
                .OrderByDescending(x => x.CreatedAt)
                .Take(500)
                .ToListAsync(ct);

            var imageRows = await imageQuery
                .OrderByDescending(x => x.CreatedAt)
                .Take(500)
                .ToListAsync(ct);

            var codeAttempts = codeRows.Select(x => new
            {
                attemptId = x.Id,
                userId = x.UserId,
                sourceKind = "code",
                kind = "code",
                language = x.Language,
                status = x.Status,
                passed = IsAccepted(x.Status),
                scorePercent = x.Score,
                codeLength = string.IsNullOrEmpty(x.Code) ? 0 : x.Code.Length,
                codeHash = HashCodeText(x.Code),
                codeSample = SampleCode(x.Code),
                fullCode = x.Code,
                createdAtUtc = x.CreatedAt,
                submittedAtUtc = x.CreatedAt
            });

            var imageAttempts = imageRows.Select(x => new
            {
                attemptId = x.Id,
                userId = (Guid?)x.UserId,
                sourceKind = "image",
                kind = "image",
                language = x.Language,
                status = x.Passed ? "passed" : "failed",
                passed = x.Passed,
                scorePercent = x.SimilarityPercent,
                codeLength = string.IsNullOrEmpty(x.Code) ? 0 : x.Code.Length,
                codeHash = HashCodeText(x.Code),
                codeSample = SampleCode(x.Code),
                fullCode = x.Code,
                createdAtUtc = x.CreatedAt,
                submittedAtUtc = x.CreatedAt
            });

            var all = codeAttempts.Concat(imageAttempts).OrderByDescending(x => x.createdAtUtc).Take(150).ToList();
            var codeUserIds = await codeQuery.Where(x => x.UserId.HasValue && x.UserId.Value != Guid.Empty).Select(x => x.UserId!.Value).Distinct().ToListAsync(ct);
            var imageUserIds = await imageQuery.Where(x => x.UserId != Guid.Empty).Select(x => x.UserId).Distinct().ToListAsync(ct);
            var acceptedCodeUserIds = await codeQuery.Where(x => x.UserId.HasValue && x.UserId.Value != Guid.Empty && x.Status != null && x.Status.ToLower() == "accepted").Select(x => x.UserId!.Value).Distinct().ToListAsync(ct);
            var passedImageUserIds = await imageQuery.Where(x => x.UserId != Guid.Empty && x.Passed).Select(x => x.UserId).Distinct().ToListAsync(ct);
            var userIds = codeUserIds.Concat(imageUserIds).Distinct().ToArray();
            var successUserIds = acceptedCodeUserIds.Concat(passedImageUserIds).Distinct().ToArray();
            var languages = all
                .Where(x => !string.IsNullOrWhiteSpace(x.language))
                .GroupBy(x => x.language)
                .Select(g => new { label = g.Key, value = g.Count() })
                .OrderByDescending(x => x.value)
                .ToArray();

            return Microsoft.AspNetCore.Http.Results.Ok(new
            {
                assignmentId,
                codeAttempts = codeCount,
                passedCodeAttempts = passedCodeCount,
                imageAttempts = imageCount,
                passedImages = passedImageCount,
                uniqueUsers = userIds.Length,
                successUsers = successUserIds.Length,
                userIds,
                languages,
                recentAttempts = all
            });
        });

        app.MapGet("/api/internal/solutions/analytics/summary", async (DateTimeOffset? fromUtc, DateTimeOffset? toUtc, int days, SolutionsDbContext db, IConfiguration cfg, IHttpClientFactory httpFactory, CancellationToken ct) =>
        {
            static bool IsAccepted(string? status) => string.Equals(status, "Accepted", StringComparison.OrdinalIgnoreCase);
            static double Percent(int num, int den) => den <= 0 ? 0 : System.Math.Round(num * 100.0 / den, 1);

            days = System.Math.Clamp(days <= 0 ? 30 : days, 1, 365);
            var to = toUtc ?? DateTimeOffset.UtcNow;
            var from = fromUtc ?? to.AddDays(-days);

            var codeRows = await db.Submissions.AsNoTracking()
                .Where(x => x.CreatedAt >= from && x.CreatedAt <= to)
                .ToListAsync(ct);
            var imageRows = await db.ImageSolutions.AsNoTracking()
                .Where(x => x.CreatedAt >= from && x.CreatedAt <= to)
                .ToListAsync(ct);

            var attempts = codeRows.Select(x => new SolutionAnalyticsAttemptRow
                {
                    AssignmentId = x.AssignmentId,
                    UserId = x.UserId,
                    Kind = "code",
                    Language = x.Language,
                    Passed = IsAccepted(x.Status),
                    Score = x.Score,
                    CreatedAt = x.CreatedAt,
                })
                .Concat(imageRows.Select(x => new SolutionAnalyticsAttemptRow
                {
                    AssignmentId = x.AssignmentId,
                    UserId = x.UserId == Guid.Empty ? (Guid?)null : x.UserId,
                    Kind = "image",
                    Language = x.Language,
                    Passed = x.Passed,
                    Score = x.SimilarityPercent,
                    CreatedAt = x.CreatedAt,
                }))
                .ToList();

            var metadata = await LoadAssignmentMetadataAsync(attempts.Select(x => x.AssignmentId), cfg, httpFactory, ct);

            List<object> DayPoints(IEnumerable<SolutionAnalyticsAttemptRow> rows, Func<IEnumerable<SolutionAnalyticsAttemptRow>, double> selector)
            {
                var byDay = rows.GroupBy(x => x.CreatedAt.UtcDateTime.Date).ToDictionary(x => x.Key, x => x.AsEnumerable());
                var start = DateTime.UtcNow.Date.AddDays(-(days - 1));
                return Enumerable.Range(0, days).Select(i =>
                {
                    var day = start.AddDays(i);
                    var value = byDay.TryGetValue(day, out var vals) ? selector(vals) : 0;
                    return (object)new { label = day.ToString("dd.MM"), date = day.ToString("yyyy-MM-dd"), value = System.Math.Round(value, 1), count = System.Math.Round(value, 1) };
                }).ToList();
            }

            var assignmentRows = attempts.GroupBy(x => x.AssignmentId).Select(g =>
            {
                metadata.TryGetValue(g.Key, out var meta);
                var total = g.Count();
                var passed = g.Count(x => x.Passed);
                var uniqueUsers = g.Where(x => x.UserId.HasValue).Select(x => x.UserId!.Value).Distinct().Count();
                var successUsers = g.Where(x => x.Passed && x.UserId.HasValue).Select(x => x.UserId!.Value).Distinct().Count();
                return new
                {
                    assignmentId = g.Key,
                    title = meta?.Title ?? "Задание без названия",
                    courseId = meta?.CourseId,
                    courseTitle = meta?.CourseTitle,
                    type = g.Any(x => x.Kind == "image") ? "image" : "code",
                    difficulty = 0,
                    rating = meta?.Rating ?? 0,
                    attempts = total,
                    passed,
                    failed = total - passed,
                    uniqueUsers,
                    stuckUsers = System.Math.Max(0, uniqueUsers - successUsers),
                    successRate = Percent(passed, total),
                    value = total,
                };
            }).ToList();

            var totalAttempts = attempts.Count;
            var passedAttempts = attempts.Count(x => x.Passed);

            return Microsoft.AspNetCore.Http.Results.Ok(new
            {
                totals = new
                {
                    totalAttempts,
                    passedAttempts,
                    failedAttempts = totalAttempts - passedAttempts,
                    successRate = Percent(passedAttempts, totalAttempts),
                    codeAttempts = attempts.Count(x => x.Kind == "code"),
                    testAttempts = 0,
                    imageAttempts = attempts.Count(x => x.Kind == "image"),
                    mathAttempts = 0,
                    avgTestScore = 0,
                },
                attemptsByDay = DayPoints(attempts, g => g.Count()),
                successByDay = DayPoints(attempts.Where(x => x.Passed), g => g.Count()),
                failureByDay = DayPoints(attempts.Where(x => !x.Passed), g => g.Count()),
                types = attempts.GroupBy(x => x.Kind).Select(g => new { label = g.Key, value = g.Count() }).OrderByDescending(x => x.value).ToList(),
                languages = attempts.Where(x => !string.IsNullOrWhiteSpace(x.Language)).GroupBy(x => x.Language).Select(g => new { label = g.Key, value = g.Count() }).OrderByDescending(x => x.value).Take(12).ToList(),
                topAssignments = assignmentRows.OrderByDescending(x => x.attempts).Take(20).ToList(),
                hardAssignments = assignmentRows.Where(x => x.attempts >= 2).OrderBy(x => x.successRate).ThenByDescending(x => x.failed).ThenByDescending(x => x.attempts).Take(20).ToList(),
            });
        });

        app.MapPost("/api/internal/users/{userId:guid}/solved-assignments", async (Guid userId, SolvedAssignmentsRequest request, SolutionsDbContext db, CancellationToken ct) =>
        {
            var ids = (request.AssignmentIds ?? Array.Empty<Guid>())
                .Where(x => x != Guid.Empty)
                .Distinct()
                .Take(2000)
                .ToArray();

            TaskForgeDebugTrace.Map("SOLVED_SERVICE_BEGIN",
                ("user", userId),
                ("requestedCount", ids.Length),
                ("requestedAssignmentIds", TaskForgeDebugTrace.MapList(ids)));

            if (ids.Length == 0)
            {
                TaskForgeDebugTrace.Map("SOLVED_SERVICE_END", ("user", userId), ("solvedCount", 0), ("solvedAssignmentIds", "-"));
                return Microsoft.AspNetCore.Http.Results.Ok(new SolvedAssignmentsResponse(userId, Array.Empty<Guid>()));
            }

            var codeSolved = await db.Submissions.AsNoTracking()
                .Where(x => x.UserId == userId && ids.Contains(x.AssignmentId) && x.Status == "Accepted")
                .Select(x => x.AssignmentId)
                .ToListAsync(ct);

            var imageSolved = await db.ImageSolutions.AsNoTracking()
                .Where(x => x.UserId == userId && ids.Contains(x.AssignmentId) && x.Passed)
                .Select(x => x.AssignmentId)
                .ToListAsync(ct);

            var solved = codeSolved.Concat(imageSolved).Distinct().ToArray();
            TaskForgeDebugTrace.Map("SOLVED_SERVICE_END",
                ("user", userId),
                ("codeSolvedCount", codeSolved.Count),
                ("codeSolvedAssignmentIds", TaskForgeDebugTrace.MapList(codeSolved)),
                ("imageSolvedCount", imageSolved.Count),
                ("imageSolvedAssignmentIds", TaskForgeDebugTrace.MapList(imageSolved)),
                ("solvedCount", solved.Length),
                ("solvedAssignmentIds", TaskForgeDebugTrace.MapList(solved)));
            return Microsoft.AspNetCore.Http.Results.Ok(new SolvedAssignmentsResponse(userId, solved));
        });

        app.MapGet("/api/internal/users/{userId:guid}/activity-summary", async (Guid userId, SolutionsDbContext db, IConfiguration cfg, IHttpClientFactory httpFactory, CancellationToken ct) =>
        {
            var codeAttempts = await db.Submissions.AsNoTracking().Where(x => x.UserId == userId).ToListAsync(ct);
            var imageAttempts = await db.ImageSolutions.AsNoTracking().Where(x => x.UserId == userId).ToListAsync(ct);
            var solvedIds = codeAttempts.Where(x => x.Status == "Accepted").Select(x => x.AssignmentId)
                .Concat(imageAttempts.Where(x => x.Passed).Select(x => x.AssignmentId))
                .Where(x => x != Guid.Empty)
                .Distinct()
                .ToArray();
            var metadata = await LoadAssignmentMetadataAsync(solvedIds, cfg, httpFactory, ct);
            var score = solvedIds.Sum(id => MetadataRating(metadata, id));
            return Microsoft.AspNetCore.Http.Results.Ok(new
            {
                solvedAssignments = solvedIds.Length,
                totalAttempts = codeAttempts.Count + imageAttempts.Count,
                codeSolutions = codeAttempts.Count,
                imageSolutions = imageAttempts.Count,
                testAttempts = 0,
                mathAttempts = 0,
                score,
                rating = score
            });
        });



        app.MapPost("/api/internal/rating/dirty-users", async (RatingDirtyUsersRequest request, SolutionsDbContext db, CancellationToken ct) =>
        {
            var ids = (request.UserIds ?? Array.Empty<Guid>())
                .Where(x => x != Guid.Empty)
                .Distinct()
                .Take(5000)
                .ToArray();
            await MarkRatingDirtyAsync(db, ids, request.Reason ?? "external-change", request.AssignmentId, ct);
            await db.SaveChangesAsync(ct);
            return Microsoft.AspNetCore.Http.Results.Ok(new { marked = ids.Length });
        });

        app.MapPost("/api/internal/rating/assignments/{assignmentId:guid}/dirty-users", async (Guid assignmentId, RatingDirtyUsersRequest request, SolutionsDbContext db, CancellationToken ct) =>
        {
            var explicitIds = (request.UserIds ?? Array.Empty<Guid>())
                .Where(x => x != Guid.Empty)
                .Distinct();

            var codeUserIds = await db.Submissions.AsNoTracking()
                .Where(x => x.AssignmentId == assignmentId && x.UserId.HasValue)
                .Select(x => x.UserId!.Value)
                .ToListAsync(ct);
            var imageUserIds = await db.ImageSolutions.AsNoTracking()
                .Where(x => x.AssignmentId == assignmentId)
                .Select(x => x.UserId)
                .ToListAsync(ct);

            var ids = explicitIds
                .Concat(codeUserIds)
                .Concat(imageUserIds)
                .Where(x => x != Guid.Empty)
                .Distinct()
                .Take(5000)
                .ToArray();

            await MarkRatingDirtyAsync(db, ids, request.Reason ?? "assignment-change", assignmentId, ct);
            await db.SaveChangesAsync(ct);
            return Microsoft.AspNetCore.Http.Results.Ok(new { assignmentId, marked = ids.Length });
        });


        return app;
    }

    private sealed class SolutionAnalyticsAttemptRow
    {
        public Guid AssignmentId { get; set; }
        public Guid? UserId { get; set; }
        public string Kind { get; set; } = "code";
        public string? Language { get; set; }
        public bool Passed { get; set; }
        public int Score { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
    }

}
