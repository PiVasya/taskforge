using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using taskforge.Services.Interfaces;

namespace taskforge.Controllers;

[ApiController]
[Route("api/admin")]
[Authorize(Roles = "Admin")]
public sealed class AdminSolutionsController : ControllerBase
{
    private readonly ISolutionAdminService _svc;
    public AdminSolutionsController(ISolutionAdminService svc) => _svc = svc;

    [HttpGet("users")]
    public async Task<IActionResult> SearchUsers([FromQuery] string q = "", [FromQuery] int take = 20)
        => Ok(await _svc.SearchUsersAsync(q, Math.Clamp(take, 1, 100)));

    [HttpGet("users/{userId:guid}/solutions")]
    public async Task<IActionResult> GetSolutionsByUser(
        [FromRoute] Guid userId,
        [FromQuery] Guid? courseId,
        [FromQuery] Guid? assignmentId,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 50)
        => Ok(await _svc.GetByUserAsync(userId, courseId, assignmentId, Math.Max(0, skip), Math.Clamp(take, 1, 200)));

    [HttpGet("solutions/{id:guid}")]
    public async Task<IActionResult> GetSolutionDetails([FromRoute] Guid id)
    {
        var dto = await _svc.GetDetailsAsync(id);
        return dto == null ? NotFound() : Ok(dto);
    }

    [HttpGet("leaderboard")]
    public async Task<IActionResult> GetLeaderboard(
        [FromQuery] Guid? courseId,
        [FromQuery] int? days,
        [FromQuery] int top = 20)
        => Ok(await _svc.GetLeaderboardAsync(courseId, days, Math.Clamp(top, 1, 100)));
}
