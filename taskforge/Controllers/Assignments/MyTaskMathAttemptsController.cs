using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using taskforge.Services.Interfaces;

namespace taskforge.Controllers
{
    [ApiController]
    [Route("api/me/math-attempts")]
    [Authorize]
    public sealed class MyTaskMathAttemptsController : ControllerBase
    {
        private readonly ITaskMathService _math;
        private readonly ICurrentUserService _current;

        public MyTaskMathAttemptsController(ITaskMathService math, ICurrentUserService current)
        {
            _math = math;
            _current = current;
        }

        [HttpGet]
        public async Task<IActionResult> GetMyAttempts(
            [FromQuery] Guid? courseId,
            [FromQuery] Guid? assignmentId,
            [FromQuery] int? days,
            [FromQuery] int skip = 0,
            [FromQuery] int take = 50,
            CancellationToken ct = default)
        {
            var userId = _current.GetUserId();
            var list = await _math.GetAttemptsAsync(userId, courseId, assignmentId, days, skip, take, ct);
            return Ok(list);
        }

        [HttpGet("{attemptId:guid}")]
        public async Task<IActionResult> GetMyAttemptReview([FromRoute] Guid attemptId, CancellationToken ct)
        {
            var userId = _current.GetUserId();
            try
            {
                var dto = await _math.GetAttemptReviewAsync(userId, attemptId, isAdmin: false, ct);
                if (dto == null) return NotFound();
                return Ok(dto);
            }
            catch (UnauthorizedAccessException)
            {
                return Forbid();
            }
        }
    }
}
