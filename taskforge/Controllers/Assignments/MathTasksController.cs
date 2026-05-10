using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using taskforge.Constants;
using taskforge.Data;
using taskforge.Data.Models.DTO.TaskMaths;
using taskforge.Filters;
using taskforge.Services.Interfaces;

namespace taskforge.Controllers.Assignments
{
    [ApiController]
    [Authorize]
    [Route("api/math-tasks")]
    public sealed class MathTasksController : ControllerBase
    {
        private readonly ITaskMathService _service;
        private readonly ICurrentUserService _current;
        private readonly ApplicationDbContext _db;
        private readonly ICourseAccessService _access;

        public MathTasksController(
            ITaskMathService service,
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

            var assignmentAccess = await _db.TaskAssignments.AsNoTracking()
                .Where(a => a.Id == assignmentId)
                .Select(a => new { a.CourseId, a.IsHidden, a.LifecycleStatus })
                .FirstOrDefaultAsync();
            if (assignmentAccess == null) return NotFound();
            if (!await _access.CanViewCourseAsync(userId, role, assignmentAccess.CourseId))
                return Forbid();
            if ((assignmentAccess.IsHidden || assignmentAccess.LifecycleStatus != "published")
                && !await _access.CanEditCourseAsync(userId, role, assignmentAccess.CourseId))
                return NotFound();

            var dto = await _service.StartAsync(assignmentId, userId, HttpContext.RequestAborted);
            return Ok(dto);
        }

        [HttpPost("{assignmentId:guid}/submit")]
        [RequireQuota(QuotaBuckets.Tasks)]
        public async Task<IActionResult> Submit(Guid assignmentId, [FromBody] TaskMathSubmitRequestDto request)
        {
            var userId = _current.GetUserId();
            var role = _current.GetRole();

            var assignmentAccess = await _db.TaskAssignments.AsNoTracking()
                .Where(a => a.Id == assignmentId)
                .Select(a => new { a.CourseId, a.IsHidden, a.LifecycleStatus })
                .FirstOrDefaultAsync();
            if (assignmentAccess == null) return NotFound();
            if (!await _access.CanViewCourseAsync(userId, role, assignmentAccess.CourseId))
                return Forbid();
            if ((assignmentAccess.IsHidden || assignmentAccess.LifecycleStatus != "published")
                && !await _access.CanEditCourseAsync(userId, role, assignmentAccess.CourseId))
                return NotFound();

            var dto = await _service.SubmitAsync(assignmentId, userId, request, HttpContext.RequestAborted);
            return Ok(dto);
        }

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
        public async Task<IActionResult> SaveEdit(Guid assignmentId, [FromBody] TaskMathEditDto dto)
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
