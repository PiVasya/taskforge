using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using taskforge.Data.Models.DTO.TaskTests;
using taskforge.Services.Interfaces;

namespace taskforge.Controllers.Assignments
{
    /// <summary>
    /// Контроллер тестовых заданий (type = "test").
    /// </summary>
    [ApiController]
    [Authorize]
    [Route("api/task-tests")]
    public sealed class TaskTestsController : ControllerBase
    {
        private readonly ITaskTestService _service;

        public TaskTestsController(ITaskTestService service)
        {
            _service = service;
        }

        [HttpPost("{assignmentId:guid}/start")]
        public async Task<IActionResult> Start(Guid assignmentId)
        {
            var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (userIdStr == null) return Unauthorized();
            var userId = Guid.Parse(userIdStr);

            var dto = await _service.StartAsync(assignmentId, userId, HttpContext.RequestAborted);
            return Ok(dto);
        }

        [HttpPost("{assignmentId:guid}/submit")]
        public async Task<IActionResult> Submit(Guid assignmentId, [FromBody] TaskTestSubmitRequestDto request)
        {
            var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (userIdStr == null) return Unauthorized();
            var userId = Guid.Parse(userIdStr);

            var dto = await _service.SubmitAsync(assignmentId, userId, request, HttpContext.RequestAborted);
            return Ok(dto);
        }

        // ===== editor =====
        [HttpGet("{assignmentId:guid}/edit")]
        public async Task<IActionResult> GetEdit(Guid assignmentId)
        {
            var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (userIdStr == null) return Unauthorized();
            var userId = Guid.Parse(userIdStr);

            var dto = await _service.GetEditAsync(assignmentId, userId, HttpContext.RequestAborted);
            return Ok(dto);
        }

        [HttpPut("{assignmentId:guid}/edit")]
        public async Task<IActionResult> SaveEdit(Guid assignmentId, [FromBody] TaskTestEditDto dto)
        {
            var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (userIdStr == null) return Unauthorized();
            var userId = Guid.Parse(userIdStr);

            await _service.SaveEditAsync(assignmentId, userId, dto, HttpContext.RequestAborted);
            return Ok();
        }
    }
}
