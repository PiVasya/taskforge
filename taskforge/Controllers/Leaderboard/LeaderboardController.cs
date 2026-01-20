// modified version of LeaderboardController.cs with filtering parameters
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
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
        private readonly IMemoryCache _cache;

        private static readonly TimeSpan RefreshCooldown = TimeSpan.FromMinutes(5);

        public LeaderboardController(ILeaderboardService svc, ICurrentUserService current, IMemoryCache cache)
        {
            _svc = svc;
            _current = current;
            _cache = cache;
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
                // Ограничение частоты: топ можно обновлять раз в 5 минут.
                // Админ может обходить ограничение.
                var role = _current.GetRole();
                var userId = _current.GetUserId();
                if (!string.Equals(role, taskforge.Constants.AppRoles.Admin, StringComparison.OrdinalIgnoreCase))
                {
                    var key = $"lb:last:{userId}";
                    if (_cache.TryGetValue<DateTime>(key, out var last))
                    {
                        var now = DateTime.UtcNow;
                        var next = last.Add(RefreshCooldown);
                        if (now < next)
                        {
                            var retrySeconds = (int)Math.Ceiling((next - now).TotalSeconds);
                            Response.Headers["Retry-After"] = retrySeconds.ToString();
                            return StatusCode(429, new { message = "Топ можно обновлять раз в 5 минут", retryAfterSeconds = retrySeconds });
                        }
                    }
                }

                var items = await _svc.GetLeaderboardAsync(_current.GetUserId(), _current.GetRole(), courseId, days, groupId, top);

                // Запоминаем успешное обновление
                if (!string.Equals(_current.GetRole(), taskforge.Constants.AppRoles.Admin, StringComparison.OrdinalIgnoreCase))
                {
                    _cache.Set($"lb:last:{_current.GetUserId()}", DateTime.UtcNow, RefreshCooldown);
                }
                return Ok(items);
            }
            catch (UnauthorizedAccessException)
            {
                return Forbid();
            }
        }
    }
}