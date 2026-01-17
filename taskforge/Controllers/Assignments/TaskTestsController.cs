using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using taskforge.Data;
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
        private readonly ICurrentUserService _current;
        private readonly ApplicationDbContext _db;
        private readonly ICourseAccessService _access;

        public TaskTestsController(
            ITaskTestService service,
            ICurrentUserService current,
            ApplicationDbContext db,
            ICourseAccessService access)
        {
            _service = service;
            _current = current;
            _db = db;
            _access = access;
        }

        [HttpPost("{assignmentId:guid}/start")]
        public async Task<IActionResult> Start(Guid assignmentId)
        {
            var userId = _current.GetUserId();
            var role = _current.GetRole();

            var courseId = await _db.TaskAssignments.AsNoTracking()
                .Where(a => a.Id == assignmentId)
                .Select(a => (Guid?)a.CourseId)
                .FirstOrDefaultAsync();
            if (courseId == null) return NotFound();
            if (!await _access.CanViewCourseAsync(userId, role, courseId.Value))
                return Forbid();

            var dto = await _service.StartAsync(assignmentId, userId, HttpContext.RequestAborted);
            return Ok(dto);
        }

        [HttpPost("{assignmentId:guid}/submit")]
        public async Task<IActionResult> Submit(Guid assignmentId, [FromBody] TaskTestSubmitRequestDto request)
        {
            var userId = _current.GetUserId();
            var role = _current.GetRole();

            var courseId = await _db.TaskAssignments.AsNoTracking()
                .Where(a => a.Id == assignmentId)
                .Select(a => (Guid?)a.CourseId)
                .FirstOrDefaultAsync();
            if (courseId == null) return NotFound();
            if (!await _access.CanViewCourseAsync(userId, role, courseId.Value))
                return Forbid();

            var dto = await _service.SubmitAsync(assignmentId, userId, request, HttpContext.RequestAborted);
            return Ok(dto);
        }

        // ===== editor =====
        [HttpGet("{assignmentId:guid}/edit")]
        public async Task<IActionResult> GetEdit(Guid assignmentId)
        {
            var userId = _current.GetUserId();
            var role = _current.GetRole();

            var courseId = await _db.TaskAssignments.AsNoTracking()
                .Where(a => a.Id == assignmentId)
                .Select(a => (Guid?)a.CourseId)
                .FirstOrDefaultAsync();
            if (courseId == null) return NotFound();
            if (!await _access.CanEditCourseAsync(userId, role, courseId.Value))
                return Forbid();

            var dto = await _service.GetEditAsync(assignmentId, userId, HttpContext.RequestAborted);
            return Ok(dto);
        }

        [HttpPut("{assignmentId:guid}/edit")]
        public async Task<IActionResult> SaveEdit(Guid assignmentId, [FromBody] TaskTestEditDto dto)
        {
            var userId = _current.GetUserId();
            var role = _current.GetRole();

            var courseId = await _db.TaskAssignments.AsNoTracking()
                .Where(a => a.Id == assignmentId)
                .Select(a => (Guid?)a.CourseId)
                .FirstOrDefaultAsync();
            if (courseId == null) return NotFound();
            if (!await _access.CanEditCourseAsync(userId, role, courseId.Value))
                return Forbid();

            await _service.SaveEditAsync(assignmentId, userId, dto, HttpContext.RequestAborted);
            return Ok();
        }
    }
}
