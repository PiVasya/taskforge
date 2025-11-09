using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using taskforge.Data.Models.DTO;
using taskforge.Services.Interfaces;

namespace taskforge.Controllers
{
    [ApiController]
    [Route("api/admin")]
    [Authorize(Roles = "Admin")]
    public sealed class AdminSolutionsController : ControllerBase
    {
        private readonly ISolutionAdminService _svc;
        public AdminSolutionsController(ISolutionAdminService svc) => _svc = svc;

        /// <summary>Поиск пользователей по email/имени/фамилии.</summary>
        /// GET /api/admin/users?q=...&take=...
        [HttpGet("users")]
        public async Task<IActionResult> SearchUsers([FromQuery] string q = "", [FromQuery] int take = 20)
            => Ok(await _svc.SearchUsersAsync(q ?? string.Empty, Math.Clamp(take, 1, 100)));

        /// <summary>Список решений пользователя (пагинация или "всё/за период").</summary>
        /// GET /api/admin/users/{userId}/solutions
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

        /// <summary>Совместимость со старым роутом списка: GET /api/admin/solutions?userId=...</summary>
        [HttpGet("solutions")]
        public Task<IActionResult> GetSolutionsByUserCompat(
            [FromQuery] Guid userId,
            [FromQuery] Guid? courseId,
            [FromQuery] Guid? assignmentId,
            [FromQuery] int skip = 0,
            [FromQuery] int take = 50,
            [FromQuery] int? days = null)
            => GetSolutionsByUser(userId, courseId, assignmentId, skip, take, days);

        /// <summary>Массовое удаление решений пользователя.</summary>
        /// DELETE /api/admin/users/{userId}/solutions
        [HttpDelete("users/{userId:guid}/solutions")]
        public async Task<IActionResult> DeleteUserSolutions(
            [FromRoute] Guid userId,
            [FromQuery] Guid? courseId,
            [FromQuery] Guid? assignmentId)
        {
            await _svc.DeleteUserSolutionsAsync(userId, courseId, assignmentId);
            return NoContent();
        }

        /// <summary>Совместимость со старым роутом удаления: DELETE /api/admin/solutions?userId=...</summary>
        [HttpDelete("solutions")]
        public Task<IActionResult> DeleteUserSolutionsCompat(
            [FromQuery] Guid userId,
            [FromQuery] Guid? courseId,
            [FromQuery] Guid? assignmentId)
            => DeleteUserSolutions(userId, courseId, assignmentId);

        /// <summary>Детали одного решения (с кодом).</summary>
        /// GET /api/admin/solutions/{id}
        [HttpGet("solutions/{id:guid}")]
        public async Task<IActionResult> GetSolutionDetails([FromRoute] Guid id)
        {
            var dto = await _svc.GetDetailsAsync(id);
            return dto == null ? NotFound() : Ok(dto);
        }

        /// <summary>Bulk-детали нескольких решений.</summary>
        /// POST /api/admin/solutions/bulk
        [HttpPost("solutions/bulk")]
        public async Task<IActionResult> GetSolutionsDetailsBulk([FromBody] Guid[] ids)
        {
            if (ids is null || ids.Length == 0)
                return Ok(Array.Empty<SolutionDetailsDto>());

            // safety-лимит, чтобы не класть БД/сервис
            var safeIds = ids.Take(200).ToArray();
            var data = await _svc.GetDetailsBulkAsync(safeIds);
            return Ok(data);
        }

        /// <summary>Лидерборд (кол-во уникально решённых заданий).</summary>
        /// GET /api/admin/leaderboard?courseId=&days=&top=
        [HttpGet("leaderboard")]
        public async Task<IActionResult> GetLeaderboard(
            [FromQuery] Guid? courseId,
            [FromQuery] int? days,
            [FromQuery] int top = 20)
            => Ok(await _svc.GetLeaderboardAsync(courseId, days, Math.Clamp(top, 1, 100)));
    }
}
