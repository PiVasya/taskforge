using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using taskforge.Data;
using taskforge.Data.Models.Entities;

namespace taskforge.Controllers.Admin;

sealed record UserPeriodSummary(int TotalUsers, int NewUsers, int ActiveUsers, int DauToday, double Retention30);
sealed record ApiPeriodSummary(int TotalRequests, int UniqueUsers, int Errors4xx, int Errors5xx, double AvgLatencyMs, double P95LatencyMs, double P99LatencyMs);
sealed record AssignmentPeriodSummary(int TotalAttempts, int CodeAttempts, int ImageAttempts, int TestAttempts, double SuccessRate, double AvgTestScore);
sealed record SupportPeriodSummary(int TotalTickets, int OpenTickets, int NewTickets, int ClosedTickets, int TotalMessages, double AvgFirstResponseMinutes, double AvgCloseMinutes);

[ApiController]
[Route("api/admin/analytics")]
[Authorize(Roles = "Admin")]
public sealed class AdminAnalyticsController : ControllerBase
{
    private static readonly TimeZoneInfo AnalyticsTimeZone = ResolveAnalyticsTimeZone();
    private readonly ApplicationDbContext _db;

    public AdminAnalyticsController(ApplicationDbContext db)
    {
        _db = db;
    }

    [HttpGet("overview")]
    public async Task<IActionResult> GetOverview([FromQuery] int days = 30, CancellationToken ct = default)
    {
        days = NormalizeDays(days);
        var nowUtc = DateTime.UtcNow;
        var localNow = ToAnalyticsTime(nowUtc);
        var localFromDate = localNow.Date.AddDays(-(days - 1));
        var fromUtc = TimeZoneInfo.ConvertTimeToUtc(localFromDate, AnalyticsTimeZone);

        var users = await BuildUsersBlockAsync(fromUtc, nowUtc, localFromDate, days, ct);
        var api = await BuildApiBlockAsync(fromUtc, nowUtc, localFromDate, days, ct);
        var assignments = await BuildAssignmentsBlockAsync(fromUtc, nowUtc, localFromDate, days, ct);
        var support = await BuildSupportBlockAsync(fromUtc, nowUtc, localFromDate, days, ct);

        var previousToUtc = fromUtc.AddTicks(-1);
        var previousLocalFromDate = localFromDate.AddDays(-days);
        var previousFromUtc = TimeZoneInfo.ConvertTimeToUtc(previousLocalFromDate, AnalyticsTimeZone);
        var currentUsersSummary = await BuildUsersSummaryAsync(fromUtc, nowUtc, ct);
        var previousUsersSummary = await BuildUsersSummaryAsync(previousFromUtc, previousToUtc, ct);
        var currentApiSummary = await BuildApiSummaryAsync(fromUtc, nowUtc, ct);
        var previousApiSummary = await BuildApiSummaryAsync(previousFromUtc, previousToUtc, ct);
        var currentAssignmentsSummary = await BuildAssignmentsSummaryAsync(fromUtc, nowUtc, ct);
        var previousAssignmentsSummary = await BuildAssignmentsSummaryAsync(previousFromUtc, previousToUtc, ct);
        var currentSupportSummary = await BuildSupportSummaryAsync(fromUtc, nowUtc, ct);
        var previousSupportSummary = await BuildSupportSummaryAsync(previousFromUtc, previousToUtc, ct);
        var executive = await BuildExecutiveBlockAsync(days, currentUsersSummary, previousUsersSummary, currentApiSummary, previousApiSummary, currentAssignmentsSummary, previousAssignmentsSummary, currentSupportSummary, previousSupportSummary, fromUtc, nowUtc, ct);

        return Ok(new
        {
            generatedAtUtc = nowUtc,
            timeZone = AnalyticsTimeZone.Id,
            period = new { days, fromUtc, toUtc = nowUtc, fromLocal = localFromDate, toLocal = localNow },
            users,
            api,
            assignments,
            support,
            executive,
        });
    }

    [HttpGet("users/{userId:guid}")]
    public async Task<IActionResult> GetUserDetails(Guid userId, [FromQuery] int days = 30, CancellationToken ct = default)
    {
        days = NormalizeDays(days);
        var nowUtc = DateTime.UtcNow;
        var localNow = ToAnalyticsTime(nowUtc);
        var localFromDate = localNow.Date.AddDays(-(days - 1));
        var fromUtc = TimeZoneInfo.ConvertTimeToUtc(localFromDate, AnalyticsTimeZone);

        var user = await _db.Users.AsNoTracking()
            .Where(x => x.Id == userId)
            .Select(x => new
            {
                x.Id,
                x.Email,
                x.FirstName,
                x.LastName,
                x.Role,
                x.CreatedAt,
                x.LastLoginAt,
            })
            .FirstOrDefaultAsync(ct);

        if (user == null) return NotFound();

        var loginRows = await _db.UserLoginLogs.AsNoTracking()
            .Where(x => x.UserId == userId && x.LoginAt >= fromUtc && x.LoginAt <= nowUtc)
            .Select(x => new { x.LoginAt, x.IpAddress, x.UserAgent })
            .ToListAsync(ct);

        var requestRows = await _db.RequestLogs.AsNoTracking()
            .Where(x => x.UserId == userId && x.CreatedAtUtc >= fromUtc && x.CreatedAtUtc <= nowUtc)
            .Select(x => new { x.Path, x.Method, x.StatusCode, x.DurationMs, x.ClientType, x.CreatedAtUtc })
            .ToListAsync(ct);

        var codeSolutions = await _db.UserTaskSolutions.AsNoTracking()
            .Where(x => x.UserId == userId && x.SubmittedAt >= fromUtc && x.SubmittedAt <= nowUtc)
            .Select(x => new { x.SubmittedAt, x.PassedAllTests, x.Language })
            .ToListAsync(ct);

        var imageSolutions = await _db.UserImageTaskSolutions.AsNoTracking()
            .Where(x => x.UserId == userId && x.CreatedAtUtc >= fromUtc && x.CreatedAtUtc <= nowUtc)
            .Select(x => new { x.CreatedAtUtc, x.Passed, x.Kind, x.Language })
            .ToListAsync(ct);

        var testAttempts = await _db.UserTaskTestAttempts.AsNoTracking()
            .Where(x => x.UserId == userId && x.CreatedAt >= fromUtc && x.CreatedAt <= nowUtc)
            .Select(x => new { x.CreatedAt, x.Passed, x.ScorePercent })
            .ToListAsync(ct);

        var supportRows = await _db.SupportTickets.AsNoTracking()
            .Where(x => x.UserId == userId && x.CreatedAt >= fromUtc && x.CreatedAt <= nowUtc)
            .Select(x => new { x.CreatedAt, x.IsClosed, x.Type })
            .ToListAsync(ct);

        var response = new
        {
            profile = new
            {
                user.Id,
                fullName = BuildFullName(user.FirstName, user.LastName),
                user.Email,
                user.Role,
                user.CreatedAt,
                user.LastLoginAt,
            },
            activity = new
            {
                totalLogins = loginRows.Count,
                activeDays = loginRows.Select(x => ToAnalyticsDate(x.LoginAt)).Distinct().Count(),
                totalRequests = requestRows.Count,
                errorRequests = requestRows.Count(x => x.StatusCode >= 400),
                avgLatencyMs = requestRows.Count == 0 ? 0 : Math.Round(requestRows.Average(x => (double)x.DurationMs), 1),
                codeSubmits = codeSolutions.Count,
                imageSubmits = imageSolutions.Count,
                testAttempts = testAttempts.Count,
                ticketsCreated = supportRows.Count,
            },
            charts = new
            {
                loginsByDay = FillDateSeries(localFromDate, days, loginRows.GroupBy(x => ToAnalyticsDate(x.LoginAt)).ToDictionary(g => g.Key, g => g.Count())),
                requestsByDay = FillDateSeries(localFromDate, days, requestRows.GroupBy(x => ToAnalyticsDate(x.CreatedAtUtc)).ToDictionary(g => g.Key, g => g.Count())),
                requestsByHour = FillHourSeries(requestRows.GroupBy(x => ToAnalyticsHour(x.CreatedAtUtc)).ToDictionary(g => g.Key, g => g.Count())),
                latencyByDay = FillDateSeriesDouble(localFromDate, days, requestRows.GroupBy(x => ToAnalyticsDate(x.CreatedAtUtc)).ToDictionary(g => g.Key, g => g.Average(v => (double)v.DurationMs))),
                solutionsByDay = FillDateSeries(localFromDate, days, codeSolutions.GroupBy(x => ToAnalyticsDate(x.SubmittedAt)).ToDictionary(g => g.Key, g => g.Count())),
            },
            topPaths = requestRows
                .GroupBy(x => x.Path)
                .Select(g => new { label = g.Key, value = g.Count(), avgLatencyMs = Math.Round(g.Average(v => (double)v.DurationMs), 1), errors = g.Count(v => v.StatusCode >= 400) })
                .OrderByDescending(x => x.value)
                .ThenBy(x => x.label)
                .Take(10)
                .ToList(),
            requestMethods = requestRows
                .GroupBy(x => x.Method)
                .Select(g => new { label = g.Key, value = g.Count() })
                .OrderByDescending(x => x.value)
                .ToList(),
            requestClients = requestRows
                .GroupBy(x => x.ClientType)
                .Select(g => new { label = g.Key, value = g.Count() })
                .OrderByDescending(x => x.value)
                .ToList(),
            languages = codeSolutions
                .Where(x => !string.IsNullOrWhiteSpace(x.Language))
                .GroupBy(x => x.Language!)
                .Select(g => new { label = g.Key, value = g.Count() })
                .OrderByDescending(x => x.value)
                .ToList(),
            recentLogins = loginRows.OrderByDescending(x => x.LoginAt).Take(10).Select(x => new { x.LoginAt, x.IpAddress, x.UserAgent }).ToList(),
            supportTypes = supportRows.GroupBy(x => x.Type).Select(g => new { label = g.Key, value = g.Count() }).OrderByDescending(x => x.value).ToList(),
        };

        return Ok(response);
    }

    [HttpGet("users/search")]
    public async Task<IActionResult> SearchUsers([FromQuery] string? q, [FromQuery] int limit = 8, CancellationToken ct = default)
    {
        q = (q ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(q)) return Ok(Array.Empty<object>());
        if (limit <= 0) limit = 8;
        if (limit > 20) limit = 20;

        var lowered = q.ToLower();
        var users = await _db.Users.AsNoTracking()
            .Where(x =>
                (!string.IsNullOrWhiteSpace(x.Email) && x.Email.ToLower().Contains(lowered)) ||
                (!string.IsNullOrWhiteSpace(x.FirstName) && x.FirstName.ToLower().Contains(lowered)) ||
                (!string.IsNullOrWhiteSpace(x.LastName) && x.LastName.ToLower().Contains(lowered)) ||
                (!string.IsNullOrWhiteSpace(x.Role) && x.Role.ToLower().Contains(lowered)))
            .OrderByDescending(x => x.LastLoginAt)
            .ThenByDescending(x => x.CreatedAt)
            .Take(limit)
            .Select(x => new
            {
                userId = x.Id,
                fullName = BuildFullName(x.FirstName, x.LastName),
                x.Email,
                x.Role,
                x.CreatedAt,
                x.LastLoginAt,
            })
            .ToListAsync(ct);

        return Ok(users);
    }

    private async Task<object> BuildUsersBlockAsync(DateTime fromUtc, DateTime nowUtc, DateTime localFromDate, int days, CancellationToken ct)
    {
        var allUsers = await _db.Users.AsNoTracking()
            .Select(x => new { x.Id, x.Email, x.FirstName, x.LastName, x.Role, x.CreatedAt, x.LastLoginAt })
            .ToListAsync(ct);

        var loginRows = await _db.UserLoginLogs.AsNoTracking()
            .Where(x => x.LoginAt >= fromUtc && x.LoginAt <= nowUtc)
            .Select(x => new { x.UserId, x.LoginAt })
            .ToListAsync(ct);

        var totalUsers = allUsers.Count;
        var newUsers = allUsers.Count(x => x.CreatedAt >= fromUtc && x.CreatedAt <= nowUtc);
        var activeUsers = loginRows.Select(x => x.UserId).Distinct().Count();
        var localToday = ToAnalyticsDate(nowUtc);
        var dauToday = loginRows.Where(x => ToAnalyticsDate(x.LoginAt) == localToday).Select(x => x.UserId).Distinct().Count();

        var usersById = allUsers.ToDictionary(x => x.Id);
        var topUsers = loginRows
            .GroupBy(x => x.UserId)
            .Select(g =>
            {
                usersById.TryGetValue(g.Key, out var u);
                return new
                {
                    userId = g.Key,
                    fullName = u == null ? g.Key.ToString() : BuildFullName(u.FirstName, u.LastName),
                    email = u?.Email,
                    role = u?.Role,
                    value = g.Count(),
                    lastLoginAt = g.Max(v => v.LoginAt),
                    activeDays = g.Select(v => ToAnalyticsDate(v.LoginAt)).Distinct().Count(),
                };
            })
            .OrderByDescending(x => x.value)
            .ThenBy(x => x.fullName)
            .Take(15)
            .ToList();

        var roleDistribution = allUsers.GroupBy(x => x.Role)
            .Select(g => new { label = string.IsNullOrWhiteSpace(g.Key) ? "Unknown" : g.Key, value = g.Count() })
            .OrderByDescending(x => x.value)
            .ToList();

        var retentionCutoff = ToAnalyticsDate(nowUtc).AddDays(-30);
        var retentionCohort = allUsers.Where(x => x.CreatedAt <= retentionCutoff).Select(x => x.Id).ToHashSet();
        var retention30 = retentionCohort.Count == 0
            ? 0
            : Math.Round(loginRows.Where(x => ToAnalyticsDate(x.LoginAt) >= retentionCutoff && retentionCohort.Contains(x.UserId)).Select(x => x.UserId).Distinct().Count() * 100.0 / retentionCohort.Count, 1);

        return new
        {
            totals = new
            {
                totalUsers,
                newUsers,
                activeUsers,
                dauToday,
                retention30,
            },
            loginsByDay = FillDateSeries(localFromDate, days, loginRows.GroupBy(x => ToAnalyticsDate(x.LoginAt)).ToDictionary(g => g.Key, g => g.Count())),
            uniqueUsersByDay = FillDateSeries(localFromDate, days, loginRows.GroupBy(x => ToAnalyticsDate(x.LoginAt)).ToDictionary(g => g.Key, g => g.Select(v => v.UserId).Distinct().Count())),
            loginsByHour = FillHourSeries(loginRows.GroupBy(x => ToAnalyticsHour(x.LoginAt)).ToDictionary(g => g.Key, g => g.Count())),
            roleDistribution,
            topUsers,
        };
    }

    private async Task<object> BuildApiBlockAsync(DateTime fromUtc, DateTime nowUtc, DateTime localFromDate, int days, CancellationToken ct)
    {
        var rows = await _db.RequestLogs.AsNoTracking()
            .Where(x => x.CreatedAtUtc >= fromUtc && x.CreatedAtUtc <= nowUtc)
            .Select(x => new { x.UserId, x.Path, x.Method, x.StatusCode, x.DurationMs, x.ClientType, x.CreatedAtUtc })
            .ToListAsync(ct);

        var userIds = rows.Where(x => x.UserId != null).Select(x => x.UserId!.Value).Distinct().ToList();
        var users = await _db.Users.AsNoTracking()
            .Where(x => userIds.Contains(x.Id))
            .Select(x => new { x.Id, x.Email, x.FirstName, x.LastName })
            .ToListAsync(ct);
        var usersById = users.ToDictionary(x => x.Id);

        var totalRequests = rows.Count;
        var avgLatency = totalRequests == 0 ? 0 : Math.Round(rows.Average(x => (double)x.DurationMs), 1);
        var p95 = CalculatePercentile(rows.Select(x => (double)x.DurationMs).OrderBy(x => x).ToArray(), 95);
        var p99 = CalculatePercentile(rows.Select(x => (double)x.DurationMs).OrderBy(x => x).ToArray(), 99);

        return new
        {
            totals = new
            {
                totalRequests,
                uniqueUsers = rows.Where(x => x.UserId != null).Select(x => x.UserId).Distinct().Count(),
                errors4xx = rows.Count(x => x.StatusCode >= 400 && x.StatusCode < 500),
                errors5xx = rows.Count(x => x.StatusCode >= 500),
                avgLatencyMs = avgLatency,
                p95LatencyMs = Math.Round(p95, 1),
                p99LatencyMs = Math.Round(p99, 1),
            },
            requestsByDay = FillDateSeries(localFromDate, days, rows.GroupBy(x => ToAnalyticsDate(x.CreatedAtUtc)).ToDictionary(g => g.Key, g => g.Count())),
            errorsByDay = FillDateSeries(localFromDate, days, rows.Where(x => x.StatusCode >= 400).GroupBy(x => ToAnalyticsDate(x.CreatedAtUtc)).ToDictionary(g => g.Key, g => g.Count())),
            latencyByDay = FillDateSeriesDouble(localFromDate, days, rows.GroupBy(x => ToAnalyticsDate(x.CreatedAtUtc)).ToDictionary(g => g.Key, g => g.Average(v => (double)v.DurationMs))),
            clientTypes = rows.GroupBy(x => x.ClientType).Select(g => new { label = g.Key, value = g.Count() }).OrderByDescending(x => x.value).ToList(),
            methodTypes = rows.GroupBy(x => x.Method).Select(g => new { label = g.Key, value = g.Count() }).OrderByDescending(x => x.value).ToList(),
            topEndpoints = rows.GroupBy(x => x.Path)
                .Select(g => new
                {
                    label = g.Key,
                    value = g.Count(),
                    avgLatencyMs = Math.Round(g.Average(v => (double)v.DurationMs), 1),
                    errors = g.Count(v => v.StatusCode >= 400),
                    errorRate = g.Count() == 0 ? 0 : Math.Round(g.Count(v => v.StatusCode >= 400) * 100.0 / g.Count(), 1),
                })
                .OrderByDescending(x => x.value)
                .ThenBy(x => x.label)
                .Take(12)
                .ToList(),
            slowEndpoints = rows.GroupBy(x => x.Path)
                .Where(g => g.Count() >= 3)
                .Select(g => new
                {
                    label = g.Key,
                    value = Math.Round(g.Average(v => (double)v.DurationMs), 1),
                    requests = g.Count(),
                })
                .OrderByDescending(x => x.value)
                .ThenByDescending(x => x.requests)
                .Take(12)
                .ToList(),
            topUsers = rows.Where(x => x.UserId != null)
                .GroupBy(x => x.UserId!.Value)
                .Select(g =>
                {
                    usersById.TryGetValue(g.Key, out var u);
                    return new
                    {
                        userId = g.Key,
                        fullName = u == null ? g.Key.ToString() : BuildFullName(u.FirstName, u.LastName),
                        email = u?.Email,
                        value = g.Count(),
                        errors = g.Count(v => v.StatusCode >= 400),
                        avgLatencyMs = Math.Round(g.Average(v => (double)v.DurationMs), 1),
                    };
                })
                .OrderByDescending(x => x.value)
                .ThenBy(x => x.fullName)
                .Take(15)
                .ToList(),
        };
    }

    private async Task<object> BuildAssignmentsBlockAsync(DateTime fromUtc, DateTime nowUtc, DateTime localFromDate, int days, CancellationToken ct)
    {
        var assignments = await _db.TaskAssignments.AsNoTracking()
            .Select(x => new { x.Id, x.Title, x.Type, x.Difficulty, x.Rating })
            .ToListAsync(ct);
        var byId = assignments.ToDictionary(x => x.Id);

        var codeSolutions = await _db.UserTaskSolutions.AsNoTracking()
            .Where(x => x.SubmittedAt >= fromUtc && x.SubmittedAt <= nowUtc)
            .Select(x => new { x.TaskAssignmentId, x.SubmittedAt, x.PassedAllTests, x.Language })
            .ToListAsync(ct);

        var imageSolutions = await _db.UserImageTaskSolutions.AsNoTracking()
            .Where(x => x.CreatedAtUtc >= fromUtc && x.CreatedAtUtc <= nowUtc)
            .Select(x => new { x.TaskAssignmentId, x.CreatedAtUtc, x.Passed, x.Kind, x.Language, x.IsTrial })
            .ToListAsync(ct);

        var testAttempts = await _db.UserTaskTestAttempts.AsNoTracking()
            .Where(x => x.CreatedAt >= fromUtc && x.CreatedAt <= nowUtc)
            .Select(x => new { x.TaskAssignmentId, x.CreatedAt, x.Passed, x.ScorePercent, x.StartedAt, x.SubmittedAt })
            .ToListAsync(ct);

        var allByDay = codeSolutions.Select(x => new { Day = ToAnalyticsDate(x.SubmittedAt), Type = "code", Passed = x.PassedAllTests })
            .Concat(imageSolutions.Select(x => new { Day = ToAnalyticsDate(x.CreatedAtUtc), Type = "image", Passed = x.Passed == true }))
            .Concat(testAttempts.Select(x => new { Day = ToAnalyticsDate(x.CreatedAt), Type = "test", Passed = x.Passed }))
            .ToList();

        var topAssignments = codeSolutions.Select(x => new { x.TaskAssignmentId, Passed = x.PassedAllTests })
            .Concat(imageSolutions.Select(x => new { x.TaskAssignmentId, Passed = x.Passed == true }))
            .Concat(testAttempts.Select(x => new { x.TaskAssignmentId, Passed = x.Passed }))
            .GroupBy(x => x.TaskAssignmentId)
            .Select(g =>
            {
                byId.TryGetValue(g.Key, out var a);
                var attempts = g.Count();
                var passed = g.Count(v => v.Passed);
                return new
                {
                    assignmentId = g.Key,
                    title = a?.Title ?? g.Key.ToString(),
                    type = a?.Type ?? "unknown",
                    attempts,
                    passed,
                    successRate = attempts == 0 ? 0 : Math.Round(passed * 100.0 / attempts, 1),
                    difficulty = a?.Difficulty ?? 0,
                    rating = a?.Rating ?? 0,
                };
            })
            .OrderByDescending(x => x.attempts)
            .Take(12)
            .ToList();

        var hardAssignments = topAssignments
            .Where(x => x.attempts >= 3)
            .OrderBy(x => x.successRate)
            .ThenByDescending(x => x.attempts)
            .Take(12)
            .ToList();

        return new
        {
            totals = new
            {
                totalAttempts = allByDay.Count,
                codeAttempts = codeSolutions.Count,
                imageAttempts = imageSolutions.Count,
                testAttempts = testAttempts.Count,
                successRate = allByDay.Count == 0 ? 0 : Math.Round(allByDay.Count(x => x.Passed) * 100.0 / allByDay.Count, 1),
                avgTestScore = testAttempts.Count == 0 ? 0 : Math.Round(testAttempts.Average(x => (double)x.ScorePercent), 1),
            },
            attemptsByDay = FillDateSeries(localFromDate, days, allByDay.GroupBy(x => x.Day).ToDictionary(g => g.Key, g => g.Count())),
            successByDay = FillDateSeries(localFromDate, days, allByDay.Where(x => x.Passed).GroupBy(x => x.Day).ToDictionary(g => g.Key, g => g.Count())),
            types = new[]
            {
                new { label = "code", value = codeSolutions.Count },
                new { label = "image", value = imageSolutions.Count },
                new { label = "test", value = testAttempts.Count },
            },
            languages = codeSolutions.Where(x => !string.IsNullOrWhiteSpace(x.Language)).Select(x => new { Language = x.Language! })
                .Concat(imageSolutions.Where(x => !string.IsNullOrWhiteSpace(x.Language)).Select(x => new { Language = x.Language! }))
                .GroupBy(x => x.Language)
                .Select(g => new { label = g.Key, value = g.Count() })
                .OrderByDescending(x => x.value)
                .Take(10)
                .ToList(),
            topAssignments,
            hardAssignments,
        };
    }

    private async Task<object> BuildSupportBlockAsync(DateTime fromUtc, DateTime nowUtc, DateTime localFromDate, int days, CancellationToken ct)
    {
        var tickets = await _db.SupportTickets.AsNoTracking()
            .Where(x => x.CreatedAt <= nowUtc)
            .Select(x => new { x.Id, x.UserId, x.Type, x.IsClosed, x.CreatedAt, x.UpdatedAt, x.AssignedAdminId })
            .ToListAsync(ct);

        var messages = await _db.SupportMessages.AsNoTracking()
            .Where(x => x.CreatedAt >= fromUtc && x.CreatedAt <= nowUtc)
            .Select(x => new { x.TicketId, x.CreatedAt, x.IsFromAdmin, x.AuthorName, x.AuthorUserId, x.Source })
            .ToListAsync(ct);

        var assignedAdminIds = tickets.Where(t => t.AssignedAdminId != null).Select(t => t.AssignedAdminId!.Value).Distinct().ToList();
        var messageAdminIds = messages.Where(m => m.AuthorUserId != null).Select(m => m.AuthorUserId!.Value).Distinct().ToList();
        var adminIds = assignedAdminIds.Concat(messageAdminIds).Distinct().ToList();

        var admins = await _db.Users.AsNoTracking()
            .Where(x => adminIds.Contains(x.Id))
            .Select(x => new { x.Id, x.FirstName, x.LastName, x.Email })
            .ToListAsync(ct);
        var adminsById = admins.ToDictionary(x => x.Id);

        var ticketsInPeriod = tickets.Where(x => x.CreatedAt >= fromUtc && x.CreatedAt <= nowUtc).ToList();
        var closedInPeriod = tickets.Where(x => x.IsClosed && x.UpdatedAt >= fromUtc && x.UpdatedAt <= nowUtc).ToList();

        var avgFirstResponse = ticketsInPeriod
            .Select(ticket =>
            {
                var firstAdmin = messages.Where(m => m.TicketId == ticket.Id && m.IsFromAdmin && m.CreatedAt >= ticket.CreatedAt)
                    .OrderBy(m => m.CreatedAt)
                    .FirstOrDefault();
                if (firstAdmin == null) return (double?)null;
                return (double?)(firstAdmin.CreatedAt - ticket.CreatedAt).TotalMinutes;
            })
            .Where(x => x != null)
            .Select(x => x!.Value)
            .ToList();

        var avgClose = closedInPeriod
            .Select(ticket => (ticket.UpdatedAt - ticket.CreatedAt).TotalMinutes)
            .ToList();

        var topAdmins = messages.Where(x => x.IsFromAdmin)
            .GroupBy(x => x.AuthorUserId ?? Guid.Empty)
            .Select(g =>
            {
                adminsById.TryGetValue(g.Key, out var a);
                return new
                {
                    adminId = g.Key == Guid.Empty ? (Guid?)null : g.Key,
                    label = a == null ? (g.FirstOrDefault()?.AuthorName ?? "Админ") : BuildFullName(a.FirstName, a.LastName),
                    email = a?.Email,
                    value = g.Count(),
                };
            })
            .OrderByDescending(x => x.value)
            .Take(12)
            .ToList();

        return new
        {
            totals = new
            {
                totalTickets = tickets.Count,
                openTickets = tickets.Count(x => !x.IsClosed),
                newTickets = ticketsInPeriod.Count,
                closedTickets = closedInPeriod.Count,
                totalMessages = messages.Count,
                avgFirstResponseMinutes = avgFirstResponse.Count == 0 ? 0 : Math.Round(avgFirstResponse.Average(), 1),
                avgCloseMinutes = avgClose.Count == 0 ? 0 : Math.Round(avgClose.Average(), 1),
            },
            ticketsByDay = FillDateSeries(localFromDate, days, ticketsInPeriod.GroupBy(x => ToAnalyticsDate(x.CreatedAt)).ToDictionary(g => g.Key, g => g.Count())),
            closedByDay = FillDateSeries(localFromDate, days, closedInPeriod.GroupBy(x => ToAnalyticsDate(x.UpdatedAt)).ToDictionary(g => g.Key, g => g.Count())),
            messagesByDay = FillDateSeries(localFromDate, days, messages.GroupBy(x => ToAnalyticsDate(x.CreatedAt)).ToDictionary(g => g.Key, g => g.Count())),
            ticketTypes = ticketsInPeriod.GroupBy(x => x.Type).Select(g => new { label = g.Key, value = g.Count() }).OrderByDescending(x => x.value).ToList(),
            topAdmins,
        };
    }

    private async Task<UserPeriodSummary> BuildUsersSummaryAsync(DateTime from, DateTime to, CancellationToken ct)
    {
        var totalUsers = await _db.Users.AsNoTracking().CountAsync(ct);
        var newUsers = await _db.Users.AsNoTracking().CountAsync(x => x.CreatedAt >= from && x.CreatedAt <= to, ct);
        var loginRows = await _db.UserLoginLogs.AsNoTracking()
            .Where(x => x.LoginAt >= from && x.LoginAt <= to)
            .Select(x => new { x.UserId, x.LoginAt })
            .ToListAsync(ct);
        var activeUsers = loginRows.Select(x => x.UserId).Distinct().Count();
        var dauToday = loginRows.Where(x => x.LoginAt.Date == to.Date).Select(x => x.UserId).Distinct().Count();
        var retentionCutoff = to.Date.AddDays(-30);
        var retentionCohort = await _db.Users.AsNoTracking().Where(x => x.CreatedAt <= retentionCutoff).Select(x => x.Id).ToListAsync(ct);
        var cohortSet = retentionCohort.ToHashSet();
        var retention30 = cohortSet.Count == 0 ? 0 : Math.Round(loginRows.Where(x => x.LoginAt >= retentionCutoff && cohortSet.Contains(x.UserId)).Select(x => x.UserId).Distinct().Count() * 100.0 / cohortSet.Count, 1);
        return new UserPeriodSummary(totalUsers, newUsers, activeUsers, dauToday, retention30);
    }

    private async Task<ApiPeriodSummary> BuildApiSummaryAsync(DateTime from, DateTime to, CancellationToken ct)
    {
        var rows = await _db.RequestLogs.AsNoTracking()
            .Where(x => x.CreatedAtUtc >= from && x.CreatedAtUtc <= to)
            .Select(x => new { x.UserId, x.StatusCode, x.DurationMs })
            .ToListAsync(ct);
        var total = rows.Count;
        var sorted = rows.Select(x => (double)x.DurationMs).OrderBy(x => x).ToArray();
        return new ApiPeriodSummary(
            total,
            rows.Where(x => x.UserId != null).Select(x => x.UserId).Distinct().Count(),
            rows.Count(x => x.StatusCode >= 400 && x.StatusCode < 500),
            rows.Count(x => x.StatusCode >= 500),
            total == 0 ? 0 : Math.Round(rows.Average(x => (double)x.DurationMs), 1),
            Math.Round(CalculatePercentile(sorted, 95), 1),
            Math.Round(CalculatePercentile(sorted, 99), 1)
        );
    }

    private async Task<AssignmentPeriodSummary> BuildAssignmentsSummaryAsync(DateTime from, DateTime to, CancellationToken ct)
    {
        var code = await _db.UserTaskSolutions.AsNoTracking().Where(x => x.SubmittedAt >= from && x.SubmittedAt <= to).Select(x => x.PassedAllTests).ToListAsync(ct);
        var image = await _db.UserImageTaskSolutions.AsNoTracking().Where(x => x.CreatedAtUtc >= from && x.CreatedAtUtc <= to).Select(x => x.Passed == true).ToListAsync(ct);
        var test = await _db.UserTaskTestAttempts.AsNoTracking().Where(x => x.CreatedAt >= from && x.CreatedAt <= to).Select(x => new { x.Passed, x.ScorePercent }).ToListAsync(ct);
        var totalAttempts = code.Count + image.Count + test.Count;
        var passed = code.Count(x => x) + image.Count(x => x) + test.Count(x => x.Passed);
        return new AssignmentPeriodSummary(totalAttempts, code.Count, image.Count, test.Count, totalAttempts == 0 ? 0 : Math.Round(passed * 100.0 / totalAttempts, 1), test.Count == 0 ? 0 : Math.Round(test.Average(x => (double)x.ScorePercent), 1));
    }

    private async Task<SupportPeriodSummary> BuildSupportSummaryAsync(DateTime from, DateTime to, CancellationToken ct)
    {
        var tickets = await _db.SupportTickets.AsNoTracking()
            .Where(x => x.CreatedAt <= to)
            .Select(x => new { x.Id, x.IsClosed, x.CreatedAt, x.UpdatedAt })
            .ToListAsync(ct);
        var ticketsInPeriod = tickets.Where(x => x.CreatedAt >= from && x.CreatedAt <= to).ToList();
        var closedInPeriod = tickets.Where(x => x.IsClosed && x.UpdatedAt >= from && x.UpdatedAt <= to).ToList();
        var messages = await _db.SupportMessages.AsNoTracking()
            .Where(x => x.CreatedAt >= from && x.CreatedAt <= to)
            .Select(x => new { x.TicketId, x.CreatedAt, x.IsFromAdmin })
            .ToListAsync(ct);
        var avgFirstResponse = ticketsInPeriod
            .Select(ticket =>
            {
                var firstAdmin = messages.Where(m => m.TicketId == ticket.Id && m.IsFromAdmin && m.CreatedAt >= ticket.CreatedAt).OrderBy(m => m.CreatedAt).FirstOrDefault();
                return firstAdmin == null ? (double?)null : (firstAdmin.CreatedAt - ticket.CreatedAt).TotalMinutes;
            })
            .Where(x => x != null)
            .Select(x => x!.Value)
            .ToList();
        var avgClose = closedInPeriod.Select(ticket => (ticket.UpdatedAt - ticket.CreatedAt).TotalMinutes).ToList();
        return new SupportPeriodSummary(
            tickets.Count,
            tickets.Count(x => !x.IsClosed),
            ticketsInPeriod.Count,
            closedInPeriod.Count,
            messages.Count,
            avgFirstResponse.Count == 0 ? 0 : Math.Round(avgFirstResponse.Average(), 1),
            avgClose.Count == 0 ? 0 : Math.Round(avgClose.Average(), 1)
        );
    }

    private async Task<object> BuildExecutiveBlockAsync(
        int days,
        UserPeriodSummary currentUsers,
        UserPeriodSummary previousUsers,
        ApiPeriodSummary currentApi,
        ApiPeriodSummary previousApi,
        AssignmentPeriodSummary currentAssignments,
        AssignmentPeriodSummary previousAssignments,
        SupportPeriodSummary currentSupport,
        SupportPeriodSummary previousSupport,
        DateTime from,
        DateTime to,
        CancellationToken ct)
    {
        var requestRows = await _db.RequestLogs.AsNoTracking()
            .Where(x => x.CreatedAtUtc >= from && x.CreatedAtUtc <= to)
            .Select(x => new { x.UserId, x.Path, x.StatusCode, x.DurationMs })
            .ToListAsync(ct);
        var requestUserIds = requestRows.Where(x => x.UserId != null).Select(x => x.UserId!.Value).Distinct().ToList();
        var requestUsers = await _db.Users.AsNoTracking().Where(x => requestUserIds.Contains(x.Id)).Select(x => new { x.Id, x.FirstName, x.LastName, x.Email }).ToListAsync(ct);
        var requestUsersById = requestUsers.ToDictionary(x => x.Id);

        var slowEndpoints = requestRows.GroupBy(x => x.Path)
            .Where(g => g.Count() >= 5)
            .Select(g => new
            {
                label = g.Key,
                value = Math.Round(g.Average(v => (double)v.DurationMs), 1),
                requests = g.Count(),
                errorRate = Math.Round(g.Count(v => v.StatusCode >= 400) * 100.0 / g.Count(), 1),
            })
            .OrderByDescending(x => x.value)
            .ThenByDescending(x => x.requests)
            .Take(6)
            .ToList();

        var noisyUsers = requestRows.Where(x => x.UserId != null)
            .GroupBy(x => x.UserId!.Value)
            .Where(g => g.Count() >= 5)
            .Select(g =>
            {
                requestUsersById.TryGetValue(g.Key, out var u);
                return new
                {
                    userId = g.Key,
                    fullName = u == null ? g.Key.ToString() : BuildFullName(u.FirstName, u.LastName),
                    email = u?.Email,
                    value = g.Count(),
                    errorRate = Math.Round(g.Count(v => v.StatusCode >= 400) * 100.0 / g.Count(), 1),
                    avgLatencyMs = Math.Round(g.Average(v => (double)v.DurationMs), 1),
                };
            })
            .OrderByDescending(x => x.value)
            .ThenByDescending(x => x.errorRate)
            .Take(6)
            .ToList();

        var assignments = await _db.TaskAssignments.AsNoTracking().Select(x => new { x.Id, x.Title, x.Type }).ToListAsync(ct);
        var assignmentsById = assignments.ToDictionary(x => x.Id);
        var code = await _db.UserTaskSolutions.AsNoTracking().Where(x => x.SubmittedAt >= from && x.SubmittedAt <= to).Select(x => new { x.TaskAssignmentId, Passed = x.PassedAllTests }).ToListAsync(ct);
        var image = await _db.UserImageTaskSolutions.AsNoTracking().Where(x => x.CreatedAtUtc >= from && x.CreatedAtUtc <= to).Select(x => new { x.TaskAssignmentId, Passed = x.Passed == true }).ToListAsync(ct);
        var test = await _db.UserTaskTestAttempts.AsNoTracking().Where(x => x.CreatedAt >= from && x.CreatedAt <= to).Select(x => new { x.TaskAssignmentId, x.Passed }).ToListAsync(ct);
        var failingAssignments = code.Select(x => new { x.TaskAssignmentId, x.Passed })
            .Concat(image.Select(x => new { x.TaskAssignmentId, x.Passed }))
            .Concat(test.Select(x => new { x.TaskAssignmentId, x.Passed }))
            .GroupBy(x => x.TaskAssignmentId)
            .Where(g => g.Count() >= 5)
            .Select(g =>
            {
                assignmentsById.TryGetValue(g.Key, out var a);
                var attempts = g.Count();
                var passed = g.Count(v => v.Passed);
                return new
                {
                    assignmentId = g.Key,
                    title = a?.Title ?? g.Key.ToString(),
                    type = a?.Type ?? "unknown",
                    attempts,
                    successRate = Math.Round(passed * 100.0 / attempts, 1),
                };
            })
            .OrderBy(x => x.successRate)
            .ThenByDescending(x => x.attempts)
            .Take(6)
            .ToList();

        var alerts = new List<object>();
        AddDeltaAlert(alerts, "Активность пользователей", currentUsers.ActiveUsers, previousUsers.ActiveUsers, "активных пользователей", 15, higherIsBad: false);
        AddDeltaAlert(alerts, "Нагрузка на backend", currentApi.TotalRequests, previousApi.TotalRequests, "API-запросов", 20, higherIsBad: false);
        AddDeltaAlert(alerts, "Успешность заданий", currentAssignments.SuccessRate, previousAssignments.SuccessRate, "% успешности", 8, higherIsBad: true, percentMetric: true);
        AddDeltaAlert(alerts, "Support backlog", currentSupport.OpenTickets, previousSupport.OpenTickets, "открытых тикетов", 15, higherIsBad: true);
        if (currentApi.Errors5xx > 0)
        {
            alerts.Add(new { severity = "high", title = "На backend есть 5xx", message = $"За {days} дн. зафиксировано {currentApi.Errors5xx} серверных ошибок. Имеет смысл посмотреть медленные или проблемные маршруты ниже." });
        }
        if (slowEndpoints.Count > 0 && slowEndpoints[0].value >= 400)
        {
            alerts.Add(new { severity = "medium", title = "Есть очень медленные маршруты", message = $"Самый тяжёлый endpoint в среднем отвечает {Math.Round((double)slowEndpoints[0].value)} мс. Проверь блок медленных маршрутов." });
        }

        return new
        {
            comparisons = new object[]
            {
                BuildComparison("Активные пользователи", currentUsers.ActiveUsers, previousUsers.ActiveUsers, "польз."),
                BuildComparison("API-запросы", currentApi.TotalRequests, previousApi.TotalRequests, "запр."),
                BuildComparison("Успешность заданий", currentAssignments.SuccessRate, previousAssignments.SuccessRate, "%", true),
                BuildComparison("Открытые тикеты", currentSupport.OpenTickets, previousSupport.OpenTickets, "шт."),
            },
            alerts,
            slowEndpoints,
            noisyUsers,
            failingAssignments,
        };
    }

    private static object BuildComparison(string label, double current, double previous, string unit, bool percentMetric = false)
    {
        var delta = previous == 0 ? (current == 0 ? 0 : 100) : Math.Round((current - previous) * 100.0 / Math.Abs(previous), 1);
        return new
        {
            label,
            current = Math.Round(current, 1),
            previous = Math.Round(previous, 1),
            deltaPercent = delta,
            trend = delta >= 0 ? "up" : "down",
            unit,
            percentMetric,
        };
    }

    private static void AddDeltaAlert(List<object> alerts, string title, double current, double previous, string noun, double thresholdPercent, bool higherIsBad, bool percentMetric = false)
    {
        if (previous == 0 && current == 0) return;
        var delta = previous == 0 ? 100 : Math.Round((current - previous) * 100.0 / Math.Abs(previous), 1);
        if (Math.Abs(delta) < thresholdPercent) return;
        var grown = delta > 0;
        var bad = higherIsBad ? grown : !grown;
        var severity = bad ? "high" : "good";
        var currentText = percentMetric ? current.ToString("0.0") + "%" : Math.Round(current).ToString();
        var previousText = percentMetric ? previous.ToString("0.0") + "%" : Math.Round(previous).ToString();
        alerts.Add(new
        {
            severity,
            title,
            message = $"Сейчас {currentText} {noun}, раньше было {previousText}. Изменение: {delta:+0.0;-0.0;0}%.",
        });
    }

    private static int NormalizeDays(int days)
    {
        if (days <= 0) return 30;
        return days switch
        {
            <= 7 => 7,
            <= 14 => 14,
            <= 30 => 30,
            <= 90 => 90,
            <= 180 => 180,
            _ => 365,
        };
    }

    private static string BuildFullName(string? firstName, string? lastName)
    {
        var value = string.Join(" ", new[] { firstName, lastName }.Where(x => !string.IsNullOrWhiteSpace(x))).Trim();
        return string.IsNullOrWhiteSpace(value) ? "Без имени" : value;
    }

    private static List<object> FillDateSeries(DateTime from, int days, Dictionary<DateTime, int> values)
    {
        var result = new List<object>(days);
        for (var i = 0; i < days; i++)
        {
            var date = from.Date.AddDays(i);
            result.Add(new { label = date.ToString("yyyy-MM-dd"), value = values.TryGetValue(date, out var v) ? v : 0 });
        }
        return result;
    }

    private static List<object> FillDateSeriesDouble(DateTime from, int days, Dictionary<DateTime, double> values)
    {
        var result = new List<object>(days);
        for (var i = 0; i < days; i++)
        {
            var date = from.Date.AddDays(i);
            result.Add(new { label = date.ToString("yyyy-MM-dd"), value = Math.Round(values.TryGetValue(date, out var v) ? v : 0, 1) });
        }
        return result;
    }

    private static List<object> FillHourSeries(Dictionary<int, int> values)
    {
        var result = new List<object>(24);
        for (var i = 0; i < 24; i++)
        {
            result.Add(new { label = i.ToString("00") + ":00", value = values.TryGetValue(i, out var v) ? v : 0 });
        }
        return result;
    }


    private static TimeZoneInfo ResolveAnalyticsTimeZone()
    {
        foreach (var id in new[] { "Europe/Warsaw", "Central European Standard Time", TimeZoneInfo.Local.Id })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch { }
        }
        return TimeZoneInfo.Utc;
    }

    private static DateTime ToAnalyticsTime(DateTime utc)
    {
        if (utc.Kind == DateTimeKind.Unspecified) utc = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        return TimeZoneInfo.ConvertTimeFromUtc(utc.ToUniversalTime(), AnalyticsTimeZone);
    }

    private static DateTime ToAnalyticsDate(DateTime utc) => ToAnalyticsTime(utc).Date;

    private static int ToAnalyticsHour(DateTime utc) => ToAnalyticsTime(utc).Hour;

    private static double CalculatePercentile(double[] sorted, double percentile)
    {
        if (sorted.Length == 0) return 0;
        if (sorted.Length == 1) return sorted[0];
        var rank = (percentile / 100.0) * (sorted.Length - 1);
        var low = (int)Math.Floor(rank);
        var high = (int)Math.Ceiling(rank);
        if (low == high) return sorted[low];
        var weight = rank - low;
        return sorted[low] * (1 - weight) + sorted[high] * weight;
    }

}
