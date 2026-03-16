using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using taskforge.Data;
using taskforge.Data.Models.Entities;

namespace taskforge.Controllers.Admin;

[ApiController]
[Route("api/admin/analytics")]
[Authorize(Roles = "Admin")]
public sealed class AdminAnalyticsController : ControllerBase
{
    private readonly ApplicationDbContext _db;

    public AdminAnalyticsController(ApplicationDbContext db)
    {
        _db = db;
    }

    [HttpGet("overview")]
    public async Task<IActionResult> GetOverview([FromQuery] int days = 30, CancellationToken ct = default)
    {
        days = NormalizeDays(days);
        var now = DateTime.UtcNow;
        var from = now.Date.AddDays(-(days - 1));

        var users = await BuildUsersBlockAsync(from, now, ct);
        var api = await BuildApiBlockAsync(from, now, ct);
        var assignments = await BuildAssignmentsBlockAsync(from, now, ct);
        var support = await BuildSupportBlockAsync(from, now, ct);

        return Ok(new
        {
            generatedAtUtc = now,
            period = new { days, fromUtc = from, toUtc = now },
            users,
            api,
            assignments,
            support,
        });
    }

    [HttpGet("users/{userId:guid}")]
    public async Task<IActionResult> GetUserDetails(Guid userId, [FromQuery] int days = 30, CancellationToken ct = default)
    {
        days = NormalizeDays(days);
        var now = DateTime.UtcNow;
        var from = now.Date.AddDays(-(days - 1));

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
            .Where(x => x.UserId == userId && x.LoginAt >= from && x.LoginAt <= now)
            .Select(x => new { x.LoginAt, x.IpAddress, x.UserAgent })
            .ToListAsync(ct);

        var requestRows = await _db.RequestLogs.AsNoTracking()
            .Where(x => x.UserId == userId && x.CreatedAtUtc >= from && x.CreatedAtUtc <= now)
            .Select(x => new { x.Path, x.Method, x.StatusCode, x.DurationMs, x.ClientType, x.CreatedAtUtc })
            .ToListAsync(ct);

        var codeSolutions = await _db.UserTaskSolutions.AsNoTracking()
            .Where(x => x.UserId == userId && x.SubmittedAt >= from && x.SubmittedAt <= now)
            .Select(x => new { x.SubmittedAt, x.PassedAllTests, x.Language })
            .ToListAsync(ct);

        var imageSolutions = await _db.UserImageTaskSolutions.AsNoTracking()
            .Where(x => x.UserId == userId && x.CreatedAtUtc >= from && x.CreatedAtUtc <= now)
            .Select(x => new { x.CreatedAtUtc, x.Passed, x.Kind, x.Language })
            .ToListAsync(ct);

        var testAttempts = await _db.UserTaskTestAttempts.AsNoTracking()
            .Where(x => x.UserId == userId && x.CreatedAt >= from && x.CreatedAt <= now)
            .Select(x => new { x.CreatedAt, x.Passed, x.ScorePercent })
            .ToListAsync(ct);

        var supportRows = await _db.SupportTickets.AsNoTracking()
            .Where(x => x.UserId == userId && x.CreatedAt >= from && x.CreatedAt <= now)
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
                activeDays = loginRows.Select(x => x.LoginAt.Date).Distinct().Count(),
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
                loginsByDay = FillDateSeries(from, days, loginRows.GroupBy(x => x.LoginAt.Date).ToDictionary(g => g.Key, g => g.Count())),
                requestsByDay = FillDateSeries(from, days, requestRows.GroupBy(x => x.CreatedAtUtc.Date).ToDictionary(g => g.Key, g => g.Count())),
                requestsByHour = FillHourSeries(requestRows.GroupBy(x => x.CreatedAtUtc.Hour).ToDictionary(g => g.Key, g => g.Count())),
                latencyByDay = FillDateSeriesDouble(from, days, requestRows.GroupBy(x => x.CreatedAtUtc.Date).ToDictionary(g => g.Key, g => g.Average(v => (double)v.DurationMs))),
                solutionsByDay = FillDateSeries(from, days, codeSolutions.GroupBy(x => x.SubmittedAt.Date).ToDictionary(g => g.Key, g => g.Count())),
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

    private async Task<object> BuildUsersBlockAsync(DateTime from, DateTime now, CancellationToken ct)
    {
        var allUsers = await _db.Users.AsNoTracking()
            .Select(x => new { x.Id, x.Email, x.FirstName, x.LastName, x.Role, x.CreatedAt, x.LastLoginAt })
            .ToListAsync(ct);

        var loginRows = await _db.UserLoginLogs.AsNoTracking()
            .Where(x => x.LoginAt >= from && x.LoginAt <= now)
            .Select(x => new { x.UserId, x.LoginAt })
            .ToListAsync(ct);

        var totalUsers = allUsers.Count;
        var newUsers = allUsers.Count(x => x.CreatedAt >= from && x.CreatedAt <= now);
        var activeUsers = loginRows.Select(x => x.UserId).Distinct().Count();
        var dauToday = loginRows.Where(x => x.LoginAt.Date == now.Date).Select(x => x.UserId).Distinct().Count();

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
                    activeDays = g.Select(v => v.LoginAt.Date).Distinct().Count(),
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

        var retentionCutoff = now.Date.AddDays(-30);
        var retentionCohort = allUsers.Where(x => x.CreatedAt <= retentionCutoff).Select(x => x.Id).ToHashSet();
        var retention30 = retentionCohort.Count == 0
            ? 0
            : Math.Round(loginRows.Where(x => x.LoginAt >= retentionCutoff && retentionCohort.Contains(x.UserId)).Select(x => x.UserId).Distinct().Count() * 100.0 / retentionCohort.Count, 1);

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
            loginsByDay = FillDateSeries(from, (now.Date - from.Date).Days + 1, loginRows.GroupBy(x => x.LoginAt.Date).ToDictionary(g => g.Key, g => g.Count())),
            uniqueUsersByDay = FillDateSeries(from, (now.Date - from.Date).Days + 1, loginRows.GroupBy(x => x.LoginAt.Date).ToDictionary(g => g.Key, g => g.Select(v => v.UserId).Distinct().Count())),
            loginsByHour = FillHourSeries(loginRows.GroupBy(x => x.LoginAt.Hour).ToDictionary(g => g.Key, g => g.Count())),
            roleDistribution,
            topUsers,
        };
    }

    private async Task<object> BuildApiBlockAsync(DateTime from, DateTime now, CancellationToken ct)
    {
        var rows = await _db.RequestLogs.AsNoTracking()
            .Where(x => x.CreatedAtUtc >= from && x.CreatedAtUtc <= now)
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
            requestsByDay = FillDateSeries(from, (now.Date - from.Date).Days + 1, rows.GroupBy(x => x.CreatedAtUtc.Date).ToDictionary(g => g.Key, g => g.Count())),
            errorsByDay = FillDateSeries(from, (now.Date - from.Date).Days + 1, rows.Where(x => x.StatusCode >= 400).GroupBy(x => x.CreatedAtUtc.Date).ToDictionary(g => g.Key, g => g.Count())),
            latencyByDay = FillDateSeriesDouble(from, (now.Date - from.Date).Days + 1, rows.GroupBy(x => x.CreatedAtUtc.Date).ToDictionary(g => g.Key, g => g.Average(v => (double)v.DurationMs))),
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

    private async Task<object> BuildAssignmentsBlockAsync(DateTime from, DateTime now, CancellationToken ct)
    {
        var assignments = await _db.TaskAssignments.AsNoTracking()
            .Select(x => new { x.Id, x.Title, x.Type, x.Difficulty, x.Rating })
            .ToListAsync(ct);
        var byId = assignments.ToDictionary(x => x.Id);

        var codeSolutions = await _db.UserTaskSolutions.AsNoTracking()
            .Where(x => x.SubmittedAt >= from && x.SubmittedAt <= now)
            .Select(x => new { x.TaskAssignmentId, x.SubmittedAt, x.PassedAllTests, x.Language })
            .ToListAsync(ct);

        var imageSolutions = await _db.UserImageTaskSolutions.AsNoTracking()
            .Where(x => x.CreatedAtUtc >= from && x.CreatedAtUtc <= now)
            .Select(x => new { x.TaskAssignmentId, x.CreatedAtUtc, x.Passed, x.Kind, x.Language, x.IsTrial })
            .ToListAsync(ct);

        var testAttempts = await _db.UserTaskTestAttempts.AsNoTracking()
            .Where(x => x.CreatedAt >= from && x.CreatedAt <= now)
            .Select(x => new { x.TaskAssignmentId, x.CreatedAt, x.Passed, x.ScorePercent, x.StartedAt, x.SubmittedAt })
            .ToListAsync(ct);

        var allByDay = codeSolutions.Select(x => new { Day = x.SubmittedAt.Date, Type = "code", Passed = x.PassedAllTests })
            .Concat(imageSolutions.Select(x => new { Day = x.CreatedAtUtc.Date, Type = "image", Passed = x.Passed == true }))
            .Concat(testAttempts.Select(x => new { Day = x.CreatedAt.Date, Type = "test", Passed = x.Passed }))
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
            attemptsByDay = FillDateSeries(from, (now.Date - from.Date).Days + 1, allByDay.GroupBy(x => x.Day).ToDictionary(g => g.Key, g => g.Count())),
            successByDay = FillDateSeries(from, (now.Date - from.Date).Days + 1, allByDay.Where(x => x.Passed).GroupBy(x => x.Day).ToDictionary(g => g.Key, g => g.Count())),
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

    private async Task<object> BuildSupportBlockAsync(DateTime from, DateTime now, CancellationToken ct)
    {
        var tickets = await _db.SupportTickets.AsNoTracking()
            .Where(x => x.CreatedAt <= now)
            .Select(x => new { x.Id, x.UserId, x.Type, x.IsClosed, x.CreatedAt, x.UpdatedAt, x.AssignedAdminId })
            .ToListAsync(ct);

        var messages = await _db.SupportMessages.AsNoTracking()
            .Where(x => x.CreatedAt >= from && x.CreatedAt <= now)
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

        var ticketsInPeriod = tickets.Where(x => x.CreatedAt >= from && x.CreatedAt <= now).ToList();
        var closedInPeriod = tickets.Where(x => x.IsClosed && x.UpdatedAt >= from && x.UpdatedAt <= now).ToList();

        var avgFirstResponse = ticketsInPeriod
            .Select(ticket =>
            {
                var firstAdmin = messages.Where(m => m.TicketId == ticket.Id && m.IsFromAdmin && m.CreatedAt >= ticket.CreatedAt)
                    .OrderBy(m => m.CreatedAt)
                    .FirstOrDefault();
                if (firstAdmin == null) return (double?)null;
                return (firstAdmin.CreatedAt - ticket.CreatedAt).TotalMinutes;
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
            ticketsByDay = FillDateSeries(from, (now.Date - from.Date).Days + 1, ticketsInPeriod.GroupBy(x => x.CreatedAt.Date).ToDictionary(g => g.Key, g => g.Count())),
            closedByDay = FillDateSeries(from, (now.Date - from.Date).Days + 1, closedInPeriod.GroupBy(x => x.UpdatedAt.Date).ToDictionary(g => g.Key, g => g.Count())),
            messagesByDay = FillDateSeries(from, (now.Date - from.Date).Days + 1, messages.GroupBy(x => x.CreatedAt.Date).ToDictionary(g => g.Key, g => g.Count())),
            ticketTypes = ticketsInPeriod.GroupBy(x => x.Type).Select(g => new { label = g.Key, value = g.Count() }).OrderByDescending(x => x.value).ToList(),
            topAdmins,
        };
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
