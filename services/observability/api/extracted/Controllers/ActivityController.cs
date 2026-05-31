using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using taskforge.Data;
using taskforge.Data.Models.Entities;

namespace taskforge.Controllers;

[ApiController]
[Route("api/activity")]
[Authorize]
public sealed class ActivityController : ControllerBase
{
    private readonly ApplicationDbContext _db;

    public ActivityController(ApplicationDbContext db)
    {
        _db = db;
    }

    public sealed record TrackPageViewRequest(string? Path, string? Title = null);

    [HttpPost("page-view")]
    public async Task<IActionResult> TrackPageView([FromBody] TrackPageViewRequest request, CancellationToken ct)
    {
        var raw = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub") ?? User.FindFirstValue("nameid");
        if (!Guid.TryParse(raw, out var userId)) return Unauthorized();

        var path = string.IsNullOrWhiteSpace(request.Path) ? "/" : request.Path.Trim();
        if (!path.StartsWith('/')) path = "/" + path;
        path = path.Length > 512 ? path[..512] : path;

        var log = new UserActionLog
        {
            UserId = userId,
            Category = "navigation",
            ActionType = "page.view",
            Source = "web",
            Method = "NAV",
            Path = path,
            Target = path,
            Description = string.IsNullOrWhiteSpace(request.Title) ? $"Открыл страницу {path}" : $"Открыл страницу {request.Title}",
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
            UserAgent = Request.Headers.UserAgent.ToString(),
            IsAuthenticated = true,
            CreatedAtUtc = DateTime.UtcNow,
        };

        _db.UserActionLogs.Add(log);
        await _db.SaveChangesAsync(ct);
        return Ok(new { ok = true });
    }
}
