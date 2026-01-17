// modified version of LeaderboardController.cs with filtering parameters
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using taskforge.Services.Interfaces;

namespace taskforge.Controllers
{
    [ApiController]
    [Route("api/leaderboard")]
    [Authorize]
    public sealed class LeaderboardController : ControllerBase
    {
        private readonly ILeaderboardService _svc;
        private readonly ICurrentUserService _current;

        public LeaderboardController(ILeaderboardService svc, ICurrentUserService current)
        {
            _svc = svc;
            _current = current;
        }

        /// <summary>Общий рейтинг пользователей.</summary>
        /// <param name="courseId">Фильтр по курсу (null — все курсы).</param>
        /// <param name="days">Количество дней для учёта решений (null — за всё время).</param>
        /// <param name="groupId">Фильтр по группе (не реализовано, оставлено для будущих расширений).</param>
        /// <param name="top">Максимальное количество записей (null — вернуть всех).</param>
        [HttpGet]
        public async Task<IActionResult> Get([
            FromQuery] Guid? courseId,
            [FromQuery] int? days,
            [FromQuery] Guid? groupId,
            [FromQuery] int? top)
        {
            try
            {
                var items = await _svc.GetLeaderboardAsync(_current.GetUserId(), _current.GetRole(), courseId, days, groupId, top);
                return Ok(items);
            }
            catch (UnauthorizedAccessException)
            {
                return Forbid();
            }
        }
    }
}