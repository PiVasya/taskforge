using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TaskForge.Tasks.Api.Contracts;
using TaskForge.Tasks.Api.Data;
using TaskForge.Tasks.Api.Services.Analytics;
using static TaskForge.Tasks.Api.Services.Common.AssignmentApiCommonService;
using static TaskForge.Tasks.Api.Services.Mapping.AssignmentApiMappingService;

namespace TaskForge.Tasks.Api.Endpoints;

internal static partial class AssignmentApiEndpoints
{
    private static WebApplication MapInsightsEndpoints(WebApplication app)
    {
        app.MapGet("/api/admin/assignments/{assignmentId:guid}/insights", async (Guid assignmentId, TasksDbContext db, IConfiguration cfg, IHttpClientFactory httpFactory, CancellationToken ct) =>
        {
            var assignment = await db.Assignments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == assignmentId, ct);
            if (assignment == null) return Microsoft.AspNetCore.Http.Results.NotFound(new { message = "Задание не найдено.", code = "ASSIGNMENT_NOT_FOUND" });

            var analyticsSettings = AssignmentAnalyticsSettingsService.FromJson(assignment.AnalyticsSettingsJson);
            var taskAttempts = await db.Attempts.AsNoTracking()
                .Where(x => x.TaskAssignmentId == assignmentId && x.SubmittedAt != null)
                .OrderByDescending(x => x.SubmittedAt)
                .Take(1000)
                .ToListAsync(ct);
            var external = await GetInternalAsync<AssignmentSolutionsInsightsDto>(httpFactory, cfg, ServiceUrl(cfg, "SolutionsApi", "http://solutions-api:8080"), $"/api/internal/assignments/{assignmentId}/attempts-summary", ct)
                ?? new AssignmentSolutionsInsightsDto();

            var activityRows = await db.AssignmentActivityEvents.AsNoTracking()
                .Where(x => x.AssignmentId == assignmentId)
                .OrderByDescending(x => x.CreatedAt)
                .Take(1000)
                .ToListAsync(ct);
            var sessions = await db.AssignmentWorkSessions.AsNoTracking()
                .Where(x => x.AssignmentId == assignmentId)
                .OrderByDescending(x => x.RiskScore)
                .ThenByDescending(x => x.LastActivityAt)
                .Take(500)
                .ToListAsync(ct);
            var snapshots = await db.AssignmentCodeSnapshots.AsNoTracking()
                .Where(x => x.AssignmentId == assignmentId && x.CodeHash != null)
                .OrderByDescending(x => x.CreatedAt)
                .Take(1000)
                .ToListAsync(ct);

            var testRows = taskAttempts.Where(x => string.Equals(x.Kind, "test", StringComparison.OrdinalIgnoreCase)).ToList();
            var mathRows = taskAttempts.Where(x => string.Equals(x.Kind, "math", StringComparison.OrdinalIgnoreCase)).ToList();
            var allUserIds = taskAttempts.Select(x => x.UserId)
                .Concat((external.UserIds ?? Array.Empty<Guid>()))
                .Concat(activityRows.Select(x => x.UserId))
                .Concat(sessions.Select(x => x.UserId))
                .Where(x => x != Guid.Empty)
                .Distinct()
                .ToArray();
            var users = await LoadUserSummariesAsync(allUserIds, cfg, httpFactory, ct);
            var courseTitle = await LoadCourseTitleAsync(assignment.CourseId, cfg, httpFactory, ct);

            var recentTaskAttempts = taskAttempts.Take(200).Select(x =>
            {
                var user = users.GetValueOrDefault(x.UserId);
                var created = x.SubmittedAt ?? x.CreatedAt;
                return new
                {
                    attemptId = (Guid?)x.Id,
                    userId = (Guid?)x.UserId,
                    login = user?.Login,
                    fullName = UserLabel(user),
                    displayName = UserLabel(user),
                    email = user?.Email ?? user?.MaskedEmail,
                    sourceKind = x.Kind,
                    kind = x.Kind,
                    language = (string?)null,
                    status = x.Passed ? "passed" : "failed",
                    passed = x.Passed,
                    scorePercent = x.ScorePercent,
                    durationSeconds = global::System.Math.Max(0, (int)global::System.Math.Round(((x.SubmittedAt ?? x.UpdatedAt) - x.StartedAt).TotalSeconds)),
                    codeLength = 0,
                    codeHash = (string?)null,
                    codeSample = (string?)null,
                    fullCode = (string?)null,
                    hasCode = false,
                    createdAtUtc = created,
                    submittedAtUtc = x.SubmittedAt
                };
            }).ToList();

            var recentExternalAttempts = (external.RecentAttempts ?? new List<AssignmentExternalAttemptDto>()).Take(200).Select(x =>
            {
                var user = x.UserId.HasValue ? users.GetValueOrDefault(x.UserId.Value) : null;
                return new
                {
                    attemptId = (Guid?)x.AttemptId,
                    userId = x.UserId,
                    login = user?.Login,
                    fullName = UserLabel(user),
                    displayName = UserLabel(user),
                    email = user?.Email ?? user?.MaskedEmail,
                    sourceKind = x.SourceKind ?? x.Kind ?? "code",
                    kind = x.Kind ?? x.SourceKind ?? "code",
                    language = x.Language,
                    status = x.Status ?? (x.Passed ? "passed" : "failed"),
                    passed = x.Passed,
                    scorePercent = x.ScorePercent,
                    durationSeconds = 0,
                    codeLength = x.CodeLength,
                    codeHash = x.CodeHash,
                    codeSample = x.CodeSample,
                    fullCode = x.FullCode,
                    hasCode = x.CodeLength > 0,
                    createdAtUtc = x.CreatedAtUtc,
                    submittedAtUtc = x.SubmittedAtUtc
                };
            }).ToList();

            var recent = recentTaskAttempts
                .Concat(recentExternalAttempts)
                .OrderByDescending(x => x.submittedAtUtc ?? x.createdAtUtc)
                .Take(250)
                .ToList();

            var attemptsByUser = recent
                .Where(x => x.userId.HasValue)
                .GroupBy(x => x.userId!.Value)
                .ToDictionary(g => g.Key, g => new
                {
                    attempts = g.Count(),
                    passed = g.Count(x => x.passed),
                    bestScore = g.Count() == 0 ? 0 : g.Max(x => x.scorePercent),
                    lastActivity = g.Max(x => x.submittedAtUtc ?? x.createdAtUtc),
                    language = g.Select(x => x.language).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))
                });
            var sessionsByUser = sessions.GroupBy(x => x.UserId).ToDictionary(g => g.Key, g => g.ToList());

            var solvers = allUserIds.Select(uid =>
            {
                attemptsByUser.TryGetValue(uid, out var attemptInfo);
                sessionsByUser.TryGetValue(uid, out var userSessions);
                userSessions ??= new List<TaskForge.Tasks.Api.Domain.AssignmentWorkSession>();
                var user = users.GetValueOrDefault(uid);
                var sessionAttempts = userSessions.Sum(x => x.SubmitCount);
                var attempts = global::System.Math.Max(attemptInfo?.attempts ?? 0, sessionAttempts);
                var passed = global::System.Math.Max(attemptInfo?.passed ?? 0, userSessions.Sum(x => x.PassedSubmitCount));
                var lastActivity = new[] { attemptInfo?.lastActivity, userSessions.Count == 0 ? (DateTimeOffset?)null : userSessions.Max(x => x.LastActivityAt) }
                    .Where(x => x.HasValue)
                    .Select(x => x!.Value)
                    .DefaultIfEmpty(DateTimeOffset.MinValue)
                    .Max();
                return new
                {
                    userId = uid,
                    login = user?.Login,
                    fullName = UserLabel(user),
                    displayName = UserLabel(user),
                    email = user?.Email ?? user?.MaskedEmail,
                    attempts,
                    passed,
                    totalAttempts = attempts,
                    successfulAttempts = passed,
                    successRate = Percent(passed, attempts),
                    bestScore = attemptInfo?.bestScore ?? 0,
                    lastActivityAtUtc = lastActivity == DateTimeOffset.MinValue ? (DateTimeOffset?)null : lastActivity,
                    language = attemptInfo?.language ?? userSessions.Select(x => x.LastLanguage).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)),
                    sessionCount = userSessions.Count,
                    riskScore = userSessions.Count == 0 ? 0 : userSessions.Max(x => x.RiskScore),
                    riskLevel = userSessions.Count == 0 ? "low" : AssignmentAnalyticsSettingsService.RiskLevel(userSessions.Max(x => x.RiskScore)),
                    pasteCount = userSessions.Sum(x => x.PasteCount),
                    copyCount = userSessions.Sum(x => x.CopyCount),
                    cutCount = userSessions.Sum(x => x.CutCount),
                    blurCount = userSessions.Sum(x => x.BlurCount),
                    hiddenCount = userSessions.Sum(x => x.HiddenCount),
                    fullscreenExitCount = userSessions.Sum(x => x.FullscreenExitCount),
                    activeDurationMs = userSessions.Sum(x => x.ActiveDurationMs),
                    totalDurationMs = userSessions.Sum(x => x.TotalDurationMs),
                    maxCodeLength = userSessions.Count == 0 ? 0 : userSessions.Max(x => x.MaxCodeLength),
                    riskReasons = userSessions.SelectMany(x => ParseReasons(x.RiskReasonsJson)).Distinct(StringComparer.OrdinalIgnoreCase).Take(10).ToArray()
                };
            }).OrderByDescending(x => x.riskScore).ThenByDescending(x => x.passed).ThenByDescending(x => x.lastActivityAtUtc).Take(300).ToList();

            var suspiciousEvents = activityRows.Where(x => x.RiskPoints > 0).Take(120).Select(x =>
            {
                var user = users.GetValueOrDefault(x.UserId);
                return new
                {
                    eventId = x.Id,
                    x.UserId,
                    displayName = UserLabel(user),
                    email = user?.Email ?? user?.MaskedEmail,
                    x.SessionId,
                    eventType = x.EventType,
                    createdAtUtc = x.CreatedAt,
                    x.CodeLength,
                    x.CodeDelta,
                    x.TextLength,
                    x.TextSample,
                    x.Language,
                    x.RiskPoints,
                    x.RiskReason
                };
            }).ToList();

            var eventStats = activityRows
                .GroupBy(x => x.EventType)
                .Select(g => new { label = g.Key, value = g.Count() })
                .OrderByDescending(x => x.value)
                .Take(30)
                .ToList();
            var statusStats = recent
                .GroupBy(x => string.IsNullOrWhiteSpace(x.status) ? "unknown" : x.status)
                .Select(g => new { label = g.Key, value = g.Count() })
                .OrderByDescending(x => x.value)
                .ToList();
            var languageStats = (external.Languages ?? new List<AssignmentLanguageStatDto>()).Count > 0
                ? (external.Languages ?? new List<AssignmentLanguageStatDto>()).Select(x => new { label = x.Label ?? "unknown", value = x.Value }).Cast<object>().ToList()
                : recent.Where(x => !string.IsNullOrWhiteSpace(x.language)).GroupBy(x => x.language).Select(g => new { label = g.Key ?? "unknown", value = g.Count() }).Cast<object>().ToList();

            var suspiciousSessions = sessions
                .Where(x => x.RiskScore > 0 || x.PasteCount > 0 || x.CopyCount > 0 || x.CutCount > 0 || x.BlurCount > 0 || x.HiddenCount > 0 || x.FullscreenExitCount > 0 || x.CodeChangeCount > 5)
                .Take(80)
                .Select(x =>
            {
                var user = users.GetValueOrDefault(x.UserId);
                return new
                {
                    x.Id,
                    x.UserId,
                    displayName = UserLabel(user),
                    email = user?.Email ?? user?.MaskedEmail,
                    x.SessionId,
                    x.StartedAt,
                    x.LastActivityAt,
                    x.FinishedAt,
                    x.TotalDurationMs,
                    x.ActiveDurationMs,
                    x.HiddenDurationMs,
                    x.BlurDurationMs,
                    x.OpenCount,
                    x.CloseCount,
                    x.HiddenCount,
                    x.BlurCount,
                    x.PasteCount,
                    x.CopyCount,
                    x.CutCount,
                    x.SubmitCount,
                    x.FailedSubmitCount,
                    x.PassedSubmitCount,
                    x.FullscreenExitCount,
                    x.MaxCodeLength,
                    x.FinalCodeLength,
                    x.LastLanguage,
                    x.RiskScore,
                    x.RiskLevel,
                    riskReasons = ParseReasons(x.RiskReasonsJson),
                    x.LastEventType
                };
            }).ToList();

            var hashRows = snapshots
                .Where(x => !string.IsNullOrWhiteSpace(x.CodeHash))
                .Select(x => new { codeHash = x.CodeHash!, userId = x.UserId, codeLength = x.CodeLength, at = x.CreatedAt })
                .Concat(recent
                    .Where(x => x.userId.HasValue && !string.IsNullOrWhiteSpace(x.codeHash))
                    .Select(x => new { codeHash = x.codeHash!, userId = x.userId!.Value, codeLength = x.codeLength, at = x.createdAtUtc }))
                .ToList();

            var similarHashes = hashRows
                .GroupBy(x => x.codeHash)
                .Where(g => g.Select(x => x.userId).Distinct().Count() > 1)
                .Select(g => new
                {
                    codeHash = g.Key,
                    users = g.Select(x => x.userId).Distinct().Count(),
                    snapshots = g.Count(),
                    maxCodeLength = g.Max(x => x.codeLength),
                    firstSeenAt = g.Min(x => x.at),
                    lastSeenAt = g.Max(x => x.at),
                    userNames = g.Select(x => x.userId).Distinct().Take(6).Select(uid => UserLabel(users.GetValueOrDefault(uid))).ToArray()
                })
                .OrderByDescending(x => x.users)
                .ThenByDescending(x => x.snapshots)
                .Take(40)
                .ToList();

            var proctoringSummary = new
            {
                events = activityRows.Count,
                sessions = sessions.Count == 0 ? activityRows.Select(x => x.SessionId).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().Count() : sessions.Count,
                users = allUserIds.Length,
                riskEvents = activityRows.Count(x => x.RiskPoints > 0),
                maxRisk = sessions.Count == 0 ? (activityRows.Count == 0 ? 0 : activityRows.Max(x => x.RiskPoints)) : sessions.Max(x => x.RiskScore),
                criticalSessions = sessions.Count(x => x.RiskScore >= 75),
                highSessions = sessions.Count(x => x.RiskScore >= 50 && x.RiskScore < 75),
                mediumSessions = sessions.Count(x => x.RiskScore >= 25 && x.RiskScore < 50),
                pasteEvents = activityRows.Count(x => x.EventType == "paste"),
                copyEvents = activityRows.Count(x => x.EventType == "copy"),
                cutEvents = activityRows.Count(x => x.EventType == "cut"),
                blurEvents = activityRows.Count(x => x.EventType == "window_blur"),
                hiddenEvents = activityRows.Count(x => x.EventType == "visibility_hidden"),
                fullscreenExitEvents = activityRows.Count(x => x.EventType == "fullscreen_exit"),
                codeJumpEvents = activityRows.Count(x => global::System.Math.Abs(x.CodeDelta ?? 0) >= 500),
                activeDurationMs = sessions.Sum(x => x.ActiveDurationMs),
                hiddenDurationMs = sessions.Sum(x => x.HiddenDurationMs),
                blurDurationMs = sessions.Sum(x => x.BlurDurationMs),
                avgActiveDurationMs = sessions.Count == 0 ? 0 : (long)sessions.Average(x => x.ActiveDurationMs),
                avgHiddenDurationMs = sessions.Count == 0 ? 0 : (long)sessions.Average(x => x.HiddenDurationMs),
                avgBlurDurationMs = sessions.Count == 0 ? 0 : (long)sessions.Average(x => x.BlurDurationMs)
            };

            var avgScore = taskAttempts.Count == 0 ? 0 : global::System.Math.Round(taskAttempts.Average(x => x.ScorePercent), 1);
            var uniqueUsers = allUserIds.Length;
            var successUsers = taskAttempts.Where(x => x.Passed).Select(x => x.UserId)
                .Concat((external.RecentAttempts ?? new List<AssignmentExternalAttemptDto>()).Where(x => x.Passed && x.UserId.HasValue).Select(x => x.UserId!.Value))
                .Distinct()
                .Count();

            return Microsoft.AspNetCore.Http.Results.Ok(new
            {
                assignmentId,
                title = assignment.Title,
                assignmentTitle = assignment.Title,
                courseId = assignment.CourseId,
                courseTitle,
                type = assignment.Type,
                language = assignment.Language,
                rating = assignment.Rating,
                difficulty = assignment.Difficulty,
                attempts = taskAttempts.Count + external.CodeAttempts + external.ImageAttempts,
                solved = taskAttempts.Count(x => x.Passed) + external.PassedCodeAttempts + external.PassedImages,
                averageScore = avgScore,
                uniqueUsers,
                successUsers,
                openedUsers = activityRows.Where(x => x.EventType == "assignment_opened").Select(x => x.UserId).Distinct().Count(),
                codeAttempts = external.CodeAttempts,
                passedCodeAttempts = external.PassedCodeAttempts,
                testAttempts = testRows.Count,
                passedTests = testRows.Count(x => x.Passed),
                imageAttempts = external.ImageAttempts,
                passedImages = external.PassedImages,
                mathAttempts = mathRows.Count,
                passedMath = mathRows.Count(x => x.Passed),
                avgReviewSeconds = taskAttempts.Count == 0 ? 0 : global::System.Math.Round(taskAttempts.Average(x => global::System.Math.Max(0, ((x.SubmittedAt ?? x.UpdatedAt) - x.StartedAt).TotalSeconds)), 1),
                avgTestScore = avgScore,
                languages = languageStats,
                statusStats,
                eventStats,
                analyticsSettings = AssignmentAnalyticsSettingsService.ToPublicDto(analyticsSettings),
                proctoringSummary,
                suspiciousEvents,
                suspiciousSessions,
                similarHashes,
                solvers,
                recentActivity = recent
            });
        });

        return app;
    }

    private static string[] ParseReasons(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<string>();
        try
        {
            return JsonSerializer.Deserialize<string[]>(json) ?? Array.Empty<string>();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }
}
