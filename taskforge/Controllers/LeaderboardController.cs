// taskforge/Controllers/LeaderboardController.cs
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
        [HttpGet]
        public async Task<IActionResult> Get()
        {
            var items = await _svc.GetLeaderboardAsync();
            return Ok(items);
        }
    }
}
