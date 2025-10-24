using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using taskforge.Data.Models.DTO;
using taskforge.Services.Interfaces;

namespace taskforge.Controllers;

[ApiController]
[Route("api/admin")]
[Authorize(Roles = "Admin")]
public sealed class AdminSolutionsController : ControllerBase
{
    private readonly ISolutionAdminService _svc;
    public AdminSolutionsController(ISolutionAdminService svc) => _svc = svc;

    /// <summary>
    /// Поиск пользователей по email/имени/фамилии.
    /// GET /api/admin/users?q=...&take=...
    /// </summary>
    [HttpGet("users")]
    public async Task<IActionResult> SearchUsers([FromQuery] string q = "", [FromQuery] int take = 20)
        => Ok(await _svc.SearchUsersAsync(q ?? string.Empty, Math.Clamp(take, 1, 100)));

    /// <summary>
    /// Список решений пользователя.
    /// GET /api/admin/users/{userId}/solutions
    /// Параметры:
    /// - courseId / assignmentId — необязательные фильтры;
    /// - skip/take — пагинация;
    /// - days — если указан (или take > 999), вернуть решения за N последних дней без пагинации (т.е. «всё сразу»).
    /// </summary>
    [HttpGet("users/{userId:guid}/solutions")]
    public async Task<IActionResult> GetSolutionsByUser(
        [FromRoute] Guid userId,
        [FromQuery] Guid? courseId,
        [FromQuery] Guid? assignmentId,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 50,
        [FromQuery] int? days = null)
    {
        // режим "всё сразу" / "за период"
        if (days.HasValue || take > 999)
        {
            var all = await _svc.GetAllByUserAsync(userId, courseId, assignmentId, days);
            return Ok(all);
        }

        // обычная пагинация
        var list = await _svc.GetByUserAsync(
            userId,
            courseId,
            assignmentId,
            Math.Max(0, skip),
            Math.Clamp(take, 1, 200));

        return Ok(list);
    }

    /// <summary>
    /// Массовое удаление решений пользователя (опционально ограничить courseId/assignmentId).
    /// DELETE /api/admin/users/{userId}/solutions
    /// </summary>
    [HttpDelete("users/{userId:guid}/solutions")]
    public async Task<IActionResult> DeleteUserSolutions(
        [FromRoute] Guid userId,
        [FromQuery] Guid? courseId,
        [FromQuery] Guid? assignmentId)
    {
        await _svc.DeleteUserSolutionsAsync(userId, courseId, assignmentId);
        return NoContent();
    }

    /// <summary>
    /// Детали одного решения (с кодом).
    /// GET /api/admin/solutions/{id}
    /// </summary>
    [HttpGet("solutions/{id:guid}")]
    public async Task<IActionResult> GetSolutionDetails([FromRoute] Guid id)
    {
        var dto = await _svc.GetDetailsAsync(id);
        return dto == null ? NotFound() : Ok(dto);
    }

    /// <summary>
    /// Лидерборд (кол-во уникально решённых заданий).
    /// GET /api/admin/leaderboard?courseId=&days=&top=
    /// </summary>
    [HttpGet("leaderboard")]
    public async Task<IActionResult> GetLeaderboard(
        [FromQuery] Guid? courseId,
        [FromQuery] int? days,
        [FromQuery] int top = 20)
        => Ok(await _svc.GetLeaderboardAsync(courseId, days, Math.Clamp(top, 1, 100)));
}
