using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using taskforge.Services.Interfaces;

namespace taskforge.Controllers
{
    /// <summary>
    /// Просмотр попыток тестов текущего пользователя (раздел «Мои решения»).
    /// </summary>
    [ApiController]
    [Route("api/me/test-attempts")]
    [Authorize]
    public sealed class MyTaskTestAttemptsController : ControllerBase
    {
        private readonly ITaskTestService _tests;
        private readonly ICurrentUserService _current;

        public MyTaskTestAttemptsController(ITaskTestService tests, ICurrentUserService current)
        {
            _tests = tests;
            _current = current;
        }

        /// <summary>
        /// Список попыток тестов текущего пользователя.
        /// GET /api/me/test-attempts?courseId=&assignmentId=&days=
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetMyAttempts(
            [FromQuery] Guid? courseId,
            [FromQuery] Guid? assignmentId,
            [FromQuery] int? days,
            CancellationToken ct)
        {
            var userId = _current.GetUserId();
            var list = await _tests.GetAttemptsAsync(userId, courseId, assignmentId, days, ct);
            return Ok(list);
        }

        /// <summary>
        /// Детали попытки для просмотра (если включено AllowReview).
        /// GET /api/me/test-attempts/{attemptId}
        /// </summary>
        [HttpGet("{attemptId:guid}")]
        public async Task<IActionResult> GetMyAttemptReview([FromRoute] Guid attemptId, CancellationToken ct)
        {
            var userId = _current.GetUserId();

            try
            {
                var dto = await _tests.GetAttemptReviewAsync(userId, attemptId, isAdmin: false, ct);
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
