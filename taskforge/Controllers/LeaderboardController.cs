// modified version of LeaderboardController.cs with filtering parameters
using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using taskforge.Services.Interfaces;

namespace taskforge.Controllers
{
    [ApiController]
    [Route("api/leaderboard")]
    [AllowAnonymous]
    public sealed class LeaderboardController : ControllerBase
    {
        private readonly ILeaderboardService _svc;

        public LeaderboardController(ILeaderboardService svc)
        {
            _svc = svc;
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
            var items = await _svc.GetLeaderboardAsync(courseId, days, groupId, top);
            return Ok(items);
        }
    }
}