using System.Collections.Concurrent;
using System.Net.Http.Json;
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
using static TaskForge.Solutions.Api.Services.Serialization.SolutionsApiSerializationService;
using static TaskForge.Solutions.Api.Services.Testing.SolutionsApiTestingService;

namespace TaskForge.Solutions.Api.Services.Results;

internal static class SolutionsApiResultsService
{
    internal static void ApplyLocalVerdict(SolutionSubmission sub, JudgeRunResult judge)
    {
        sub.Status = judge.Verdict;
        sub.Score = judge.Score;
        sub.ResultJson = JsonSerializer.Serialize(new
        {
            verdict = judge.Verdict,
            status = judge.Verdict,
            pending = false,
            message = judge.Message,
            passedAllTests = judge.PassedAllTests,
            compileError = judge.CompileError,
            results = judge.Results,
            cases = judge.Results,
            raw = judge.Raw
        }, JsonOptions());
    }

    internal static int MetadataRating(Dictionary<Guid, AssignmentMetadata> metadata, Guid assignmentId)
        => metadata.TryGetValue(assignmentId, out var m) && m.Rating > 0 ? m.Rating : 1;

    internal static async Task<List<LeaderboardActivityRow>> LoadTaskLeaderboardRowsAsync(Guid? courseId, int? days, Guid[]? userIds, IConfiguration cfg, IHttpClientFactory httpFactory, CancellationToken ct)
    {
        var rows = await PostInternalAsync<List<TaskActivityRowDto>>(httpFactory, cfg, ServiceUrl(cfg, "TasksApi", "http://tasks-api:8080"), "/api/internal/activity/leaderboard", new ActivityLeaderboardRequest(courseId, days, userIds), ct) ?? new List<TaskActivityRowDto>();
        return rows.Where(x => x.UserId != Guid.Empty && x.AssignmentId != Guid.Empty)
            .Select(x => new LeaderboardActivityRow(x.UserId, x.AssignmentId, System.Math.Max(1, x.Rating), x.SubmittedAt, x.Kind ?? "task"))
            .ToList();
    }

    internal static async Task<Dictionary<Guid, List<object>>> LoadBadgeMapAsync(SolutionsDbContext db, IEnumerable<Guid> userIds, CancellationToken ct)
    {
        var ids = userIds.Where(x => x != Guid.Empty).Distinct().ToArray();
        if (ids.Length == 0) return new Dictionary<Guid, List<object>>();
        var userBadges = await db.UserBadges.AsNoTracking().Where(x => ids.Contains(x.UserId)).ToListAsync(ct);
        var badgeIds = userBadges.Select(x => x.BadgeId).Distinct().ToArray();
        var badges = await db.Badges.AsNoTracking().Where(x => badgeIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
        return userBadges
            .Where(x => badges.ContainsKey(x.BadgeId))
            .GroupBy(x => x.UserId)
            .ToDictionary(g => g.Key, g => g.Select(x => badges[x.BadgeId]).Select(b => (object)new { b.Id, b.Name, b.Description, b.ImageUrl }).ToList());
    }

    internal static int UserSummarySearchScore(UserSummaryDto? user, Guid id, string query)
    {
        var values = new[] { id.ToString(), user?.Login, user?.MaskedEmail, user?.DisplayName, user?.FirstName, user?.LastName }
            .Select(NormalizeSearch)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToArray();
        if (values.Any(x => x.Contains(query, StringComparison.OrdinalIgnoreCase))) return 0;
        var best = int.MaxValue;
        foreach (var value in values)
        {
            best = System.Math.Min(best, Levenshtein(value, query));
            foreach (var token in value.Split(new[] { ' ', '@', '.', '_', '-', '+' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) best = System.Math.Min(best, Levenshtein(token, query));
        }
        return best == int.MaxValue ? 999 : best;
    }

    internal static bool IsTerminalVerdict(string? value) => value is not null && (string.Equals(value, "Accepted", StringComparison.OrdinalIgnoreCase) || string.Equals(value, "Rejected", StringComparison.OrdinalIgnoreCase) || string.Equals(value, "CompileError", StringComparison.OrdinalIgnoreCase) || string.Equals(value, "PolicyFailed", StringComparison.OrdinalIgnoreCase) || string.Equals(value, "NoTestsConfigured", StringComparison.OrdinalIgnoreCase) || string.Equals(value, "JudgeUnavailable", StringComparison.OrdinalIgnoreCase) || string.Equals(value, "LanguageNotAllowed", StringComparison.OrdinalIgnoreCase));

    internal static bool IsPendingVerdict(string? value) => value is not null && (string.Equals(value, "Preparing", StringComparison.OrdinalIgnoreCase) || string.Equals(value, "Queued", StringComparison.OrdinalIgnoreCase) || string.Equals(value, "Running", StringComparison.OrdinalIgnoreCase));

    internal static string CleanVerdict(string? value) => string.IsNullOrWhiteSpace(value) ? "Rejected" : value.Trim();

    internal static bool AffectsRatingStatus(string? status)
        => IsTerminalVerdict(status) || string.Equals(status, "Accepted", StringComparison.OrdinalIgnoreCase);

    internal static async Task MarkRatingDirtyAsync(SolutionsDbContext db, Guid userId, string reason, Guid? assignmentId = null, CancellationToken ct = default)
    {
        if (userId == Guid.Empty) return;

        var now = DateTimeOffset.UtcNow;
        var dirty = await db.RatingDirtyUsers.FirstOrDefaultAsync(x => x.UserId == userId, ct);
        if (dirty == null)
        {
            db.RatingDirtyUsers.Add(new RatingDirtyUser
            {
                UserId = userId,
                Reason = string.IsNullOrWhiteSpace(reason) ? "changed" : reason.Trim()[..System.Math.Min(reason.Trim().Length, 200)],
                AssignmentId = assignmentId,
                MarkedAtUtc = now
            });
            return;
        }

        dirty.Reason = string.IsNullOrWhiteSpace(reason) ? dirty.Reason : reason.Trim()[..System.Math.Min(reason.Trim().Length, 200)];
        dirty.AssignmentId = assignmentId ?? dirty.AssignmentId;
        dirty.MarkedAtUtc = now;
    }

    internal static async Task MarkRatingDirtyAsync(SolutionsDbContext db, IEnumerable<Guid> userIds, string reason, Guid? assignmentId = null, CancellationToken ct = default)
    {
        var ids = userIds.Where(x => x != Guid.Empty).Distinct().Take(5000).ToArray();
        if (ids.Length == 0) return;

        var existing = await db.RatingDirtyUsers.Where(x => ids.Contains(x.UserId)).ToDictionaryAsync(x => x.UserId, ct);
        var now = DateTimeOffset.UtcNow;
        var cleanReason = string.IsNullOrWhiteSpace(reason) ? "changed" : reason.Trim()[..System.Math.Min(reason.Trim().Length, 200)];
        foreach (var id in ids)
        {
            if (existing.TryGetValue(id, out var row))
            {
                row.Reason = cleanReason;
                row.AssignmentId = assignmentId ?? row.AssignmentId;
                row.MarkedAtUtc = now;
            }
            else
            {
                db.RatingDirtyUsers.Add(new RatingDirtyUser
                {
                    UserId = id,
                    Reason = cleanReason,
                    AssignmentId = assignmentId,
                    MarkedAtUtc = now
                });
            }
        }
    }

    internal static async Task AddRating(SolutionsDbContext db, Guid userId, int score, bool accepted)
    {
        await MarkRatingDirtyAsync(db, userId, accepted ? "accepted-verdict" : "terminal-verdict");
    }

    internal static async Task<object> GetQuotaStatus(SolutionsDbContext db, Guid userId)
    {
        var tasks = await StatusFor(db, userId, "tasks", 10, TimeSpan.FromSeconds(90));
        var top = await StatusFor(db, userId, "top", 5, TimeSpan.FromMinutes(30));
        return new { enabled = true, tasks, top, buckets = new[] { tasks, top }, remaining = tasks.remaining, capacity = tasks.capacity };
    }

    internal static object ToTopSolutionDto(SolutionSubmission x, bool includeCode, AssignmentMetadata? metadata = null, UserSummaryDto? user = null)
    {
        var result = SanitizeSolutionResult(ParseJsonElement(x.ResultJson), includeHiddenDetails: false);
        var counts = CountCases(result);
        return new
        {
            x.Id,
            x.AssignmentId,
            assignmentTitle = metadata?.Title,
            title = metadata?.Title,
            courseId = metadata?.CourseId,
            courseTitle = metadata?.CourseTitle,
            x.UserId,
            login = user?.Login,
            userName = UserLabel(user),
            displayName = UserLabel(user),
            email = user?.MaskedEmail,
            maskedEmail = user?.MaskedEmail,
            x.Language,
            code = includeCode ? x.Code : null,
            submittedCode = includeCode ? x.Code : null,
            verdict = x.Status,
            status = x.Status,
            x.Score,
            result = result.HasValue ? (object)result.Value : null,
            passedCount = counts.passed,
            failedCount = counts.failed,
            totalCount = counts.total,
            codeHiddenUntilSolved = !includeCode,
            x.CreatedAt,
            createdAtUtc = x.CreatedAt,
            submittedAt = x.CreatedAt
        };
    }

    internal static JsonElement? SanitizeSolutionResult(JsonElement? result, bool includeHiddenDetails)
    {
        if (includeHiddenDetails || !result.HasValue) return result;
        try
        {
            var node = JsonNode.Parse(result.Value.GetRawText());
            RemoveHiddenTestNodes(node);
            return JsonSerializer.SerializeToElement(node, JsonOptions());
        }
        catch
        {
            // Fail closed: if we cannot safely remove hidden tests, do not return
            // potentially sensitive result payload to a regular user.
            return null;
        }
    }

    internal static object ToSubmitDto(SolutionSubmission x, bool includeSensitiveResult = false) => ToDto(x, includeSensitiveResult);

    internal static object BadgeDto(Badge x) => new { x.Id, x.Name, x.Description, x.ImageUrl, x.CreatedAt };

}
