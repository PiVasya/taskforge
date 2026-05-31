// taskforge/Controllers/UsersController.cs
using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using taskforge.Services.Interfaces;

namespace taskforge.Controllers
{
    [ApiController]
    [Route("api/users")]
    [AllowAnonymous]
    public sealed class UsersController : ControllerBase
    {
        private readonly ILeaderboardService _leaderboard;

        public UsersController(ILeaderboardService leaderboard)
        {
            _leaderboard = leaderboard;
        }

        /// <summary>Публичный профиль пользователя (для просмотра из топа).</summary>
        [HttpGet("{id:guid}/public-profile")]
        public async Task<IActionResult> GetPublicProfile(Guid id)
        {
            var dto = await _leaderboard.GetPublicProfileAsync(id);
            if (dto == null)
                return NotFound();

            return Ok(dto);
        }
    }
}
