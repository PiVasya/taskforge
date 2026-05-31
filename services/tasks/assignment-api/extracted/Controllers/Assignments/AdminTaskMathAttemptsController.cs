using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using taskforge.Services.Interfaces;

namespace taskforge.Controllers
{
    [ApiController]
    [Route("api/admin")]
    [Authorize(Roles = "Admin")]
    public sealed class AdminTaskMathAttemptsController : ControllerBase
    {
        private readonly ITaskMathService _math;

        public AdminTaskMathAttemptsController(ITaskMathService math)
        {
            _math = math;
        }

        [HttpGet("users/{userId:guid}/math-attempts")]
        public async Task<IActionResult> GetUserAttempts(
            [FromRoute] Guid userId,
            [FromQuery] Guid? courseId,
            [FromQuery] Guid? assignmentId,
            [FromQuery] int? days,
            [FromQuery] int skip = 0,
            [FromQuery] int take = 50,
            CancellationToken ct = default)
        {
            var list = await _math.GetAttemptsAsync(userId, courseId, assignmentId, days, skip, take, ct);
            return Ok(list);
        }

        [HttpGet("math-attempts/{attemptId:guid}")]
        public async Task<IActionResult> GetAttemptReview([FromRoute] Guid attemptId, CancellationToken ct)
        {
            var dto = await _math.GetAttemptReviewAsync(Guid.Empty, attemptId, isAdmin: true, ct);
            return dto == null ? NotFound() : Ok(dto);
        }

        [HttpDelete("math-attempts/{attemptId:guid}")]
        public async Task<IActionResult> DeleteAttempt([FromRoute] Guid attemptId, CancellationToken ct)
        {
            await _math.DeleteAttemptAsync(attemptId, ct);
            return NoContent();
        }
    }
}
