using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using taskforge.Services.Interfaces;

namespace taskforge.Controllers
{
    /// <summary>
    /// Просмотр истории решений текущего пользователя.
    /// </summary>
    [ApiController]
    [Route("api/me/solutions")]
    [Authorize]
    public sealed class MySolutionsController : ControllerBase
    {
        private readonly ISolutionService _solutions;
        private readonly ICurrentUserService _current;

        public MySolutionsController(ISolutionService solutions, ICurrentUserService current)
        {
            _solutions = solutions;
            _current = current;
        }

        /// <summary>Список решений текущего пользователя.</summary>
        [HttpGet]
        public async Task<IActionResult> GetMySolutions(
            [FromQuery] Guid? courseId,
            [FromQuery] Guid? assignmentId,
            [FromQuery] int skip = 0,
            [FromQuery] int take = 50,
            [FromQuery] int? days = null)
        {
            var userId = _current.GetUserId();

            if (days.HasValue || take > 999)
            {
                var all = await _solutions.GetMySolutionsAllAsync(userId, courseId, assignmentId, days);
                return Ok(all);
            }

            var list = await _solutions.GetMySolutionsAsync(
                userId,
                courseId,
                assignmentId,
                Math.Max(0, skip),
                Math.Clamp(take, 1, 200));

            return Ok(list);
        }

        /// <summary>Детали одного решения текущего пользователя.</summary>
        [HttpGet("{id:guid}")]
        public async Task<IActionResult> GetMySolutionDetails([FromRoute] Guid id)
        {
            var userId = _current.GetUserId();
            var dto = await _solutions.GetMySolutionDetailsAsync(userId, id);
            if (dto == null)
                return NotFound();

            return Ok(dto);
        }
    }
}
