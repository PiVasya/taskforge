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
    public sealed class AdminTaskTestAttemptsController : ControllerBase
    {
        private readonly ITaskTestService _tests;

        public AdminTaskTestAttemptsController(ITaskTestService tests)
        {
            _tests = tests;
        }

        /// <summary>
        /// Список попыток тестов пользователя.
        /// GET /api/admin/users/{userId}/test-attempts?courseId=&assignmentId=&days=
        /// </summary>
        [HttpGet("users/{userId:guid}/test-attempts")]
        public async Task<IActionResult> GetUserAttempts(
            [FromRoute] Guid userId,
            [FromQuery] Guid? courseId,
            [FromQuery] Guid? assignmentId,
            [FromQuery] int? days,
            CancellationToken ct)
        {
            var list = await _tests.GetAttemptsAsync(userId, courseId, assignmentId, days, ct);
            return Ok(list);
        }

        /// <summary>
        /// Детали попытки (просмотр админом).
        /// GET /api/admin/test-attempts/{attemptId}
        /// </summary>
        [HttpGet("test-attempts/{attemptId:guid}")]
        public async Task<IActionResult> GetAttemptReview([FromRoute] Guid attemptId, CancellationToken ct)
        {
            // userId тут не важен, потому что isAdmin=true (проверка владельца не применяется)
            var dto = await _tests.GetAttemptReviewAsync(Guid.Empty, attemptId, isAdmin: true, ct);
            return dto == null ? NotFound() : Ok(dto);
        }
    }
}
