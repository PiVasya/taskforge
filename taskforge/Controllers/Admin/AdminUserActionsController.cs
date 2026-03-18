using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using taskforge.Data;

namespace taskforge.Controllers.Admin;

[ApiController]
[Route("api/admin/activity")]
[Authorize(Roles = "Admin")]
public sealed class AdminUserActionsController : ControllerBase
{
    private readonly ApplicationDbContext _db;

    public AdminUserActionsController(ApplicationDbContext db)
    {
        _db = db;
    }

    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] int days = 7,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        [FromQuery] string? q = null,
        [FromQuery] string? category = null,
        [FromQuery] string? actionType = null,
        [FromQuery] string? source = null,
        [FromQuery] Guid? userId = null,
        CancellationToken ct = default)
    {
        days = Math.Clamp(days, 1, 365);
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 10, 200);

        var nowUtc = DateTime.UtcNow;
        var fromUtc = nowUtc.AddDays(-days);

        var query = _db.UserActionLogs.AsNoTracking()
            .Include(x => x.User)
            .Where(x => x.CreatedAtUtc >= fromUtc && x.CreatedAtUtc <= nowUtc);

        if (userId != null) query = query.Where(x => x.UserId == userId);
        if (!string.IsNullOrWhiteSpace(category)) query = query.Where(x => x.Category == category);
        if (!string.IsNullOrWhiteSpace(actionType)) query = query.Where(x => x.ActionType == actionType);
        if (!string.IsNullOrWhiteSpace(source)) query = query.Where(x => x.Source == source);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var lowered = q.Trim().ToLower();
            query = query.Where(x =>
                (x.Path != null && x.Path.ToLower().Contains(lowered)) ||
                (x.Target != null && x.Target.ToLower().Contains(lowered)) ||
                (x.Description != null && x.Description.ToLower().Contains(lowered)) ||
                (x.ActionType != null && x.ActionType.ToLower().Contains(lowered)) ||
                (x.Category != null && x.Category.ToLower().Contains(lowered)) ||
                (x.User != null && (
                    (!string.IsNullOrWhiteSpace(x.User.Email) && x.User.Email.ToLower().Contains(lowered)) ||
                    (!string.IsNullOrWhiteSpace(x.User.FirstName) && x.User.FirstName.ToLower().Contains(lowered)) ||
                    (!string.IsNullOrWhiteSpace(x.User.LastName) && x.User.LastName.ToLower().Contains(lowered))
                )));
        }

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(x => x.CreatedAtUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new
            {
                x.Id,
                x.Category,
                x.ActionType,
                x.Source,
                x.Method,
                x.Path,
                x.Target,
                x.Description,
                x.StatusCode,
                x.IsAuthenticated,
                x.IpAddress,
                x.CreatedAtUtc,
                user = x.User == null ? null : new
                {
                    x.User.Id,
                    fullName = ((x.User.FirstName ?? "") + " " + (x.User.LastName ?? "")).Trim(),
                    x.User.Email,
                    x.User.Role,
                }
            })
            .ToListAsync(ct);

        var topCategories = await query.GroupBy(x => x.Category)
            .Select(g => new { label = g.Key, value = g.Count() })
            .OrderByDescending(x => x.value)
            .Take(10)
            .ToListAsync(ct);

        var topActions = await query.GroupBy(x => x.ActionType)
            .Select(g => new { label = g.Key, value = g.Count() })
            .OrderByDescending(x => x.value)
            .Take(10)
            .ToListAsync(ct);

        var topUsers = await query.Where(x => x.UserId != null)
            .Select(x => new { x.UserId, Email = x.User != null ? x.User.Email : null, FirstName = x.User != null ? x.User.FirstName : null, LastName = x.User != null ? x.User.LastName : null, Role = x.User != null ? x.User.Role : null })
            .GroupBy(x => new { x.UserId, x.Email, x.FirstName, x.LastName, x.Role })
            .Select(g => new
            {
                userId = g.Key.UserId,
                fullName = ((g.Key.FirstName ?? "") + " " + (g.Key.LastName ?? "")).Trim(),
                email = g.Key.Email,
                role = g.Key.Role,
                value = g.Count(),
            })
            .OrderByDescending(x => x.value)
            .Take(10)
            .ToListAsync(ct);

        return Ok(new
        {
            period = new { days, fromUtc, toUtc = nowUtc },
            paging = new { page, pageSize, total },
            totals = new
            {
                totalActions = total,
                uniqueUsers = await query.Where(x => x.UserId != null).Select(x => x.UserId).Distinct().CountAsync(ct),
                errors = await query.CountAsync(x => x.StatusCode >= 400, ct),
                navigations = await query.CountAsync(x => x.Category == "navigation", ct),
            },
            filters = new { q, category, actionType, source, userId },
            topCategories,
            topActions,
            topUsers,
            items,
        });
    }
}
