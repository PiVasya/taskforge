using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using taskforge.Data;
using taskforge.Data.Models.DTO;
using taskforge.Services.Interfaces;
using taskforge.Constants;

namespace taskforge.Controllers
{
    [ApiController]
    [Route("api")]
    [Authorize]
    public sealed class AssignmentsController : ControllerBase
    {
        private readonly IAssignmentService _assignments;
        private readonly ICurrentUserService _current;
        private readonly ApplicationDbContext _db;
        private readonly ICourseAccessService _access;
        private readonly ILogger<AssignmentsController> _log;

        public AssignmentsController(
            IAssignmentService assignments,
            ICurrentUserService current,
            ApplicationDbContext db,
            ICourseAccessService access,
            ILogger<AssignmentsController> log)
        {
            _assignments = assignments;
            _current = current;
            _db = db;
            _access = access;
            _log = log;
        }

        [HttpPost("courses/{courseId:guid}/assignments")]
        public async Task<IActionResult> Create([FromRoute] Guid courseId, [FromBody] CreateAssignmentRequest req)
        {
            var uid = _current.GetUserId();
            var role = _current.GetRole();

            if (!await _access.CanEditCourseAsync(uid, role, courseId))
                return Forbid();

            var id = await _assignments.CreateAsync(courseId, req, uid);
            return CreatedAtAction(nameof(GetById), new { assignmentId = id }, new { id });
        }

        [HttpGet("courses/{courseId:guid}/assignments")]
        public async Task<IActionResult> ListByCourse([FromRoute] Guid courseId)
        {
            var uid = _current.GetUserId();
            var role = _current.GetRole();

            if (!await _access.CanViewCourseAsync(uid, role, courseId))
                return Forbid();

            var list = await _assignments.GetByCourseAsync(courseId, uid);
            return Ok(list);
        }

        [HttpGet("assignments/{assignmentId:guid}")]
        public async Task<IActionResult> GetById([FromRoute] Guid assignmentId)
        {
            var uid = _current.GetUserId();
            var role = _current.GetRole();

            var courseId = await _db.TaskAssignments.AsNoTracking()
                .Where(a => a.Id == assignmentId)
                .Select(a => (Guid?)a.CourseId)
                .FirstOrDefaultAsync();

            if (courseId == null) return NotFound();
            if (!await _access.CanViewCourseAsync(uid, role, courseId.Value))
                return Forbid();

            var dto = await _assignments.GetDetailsAsync(assignmentId, uid);
            if (dto == null) return NotFound();
            return Ok(dto);
        }

        [HttpPut("assignments/{assignmentId:guid}")]
        public async Task<IActionResult> Update([FromRoute] Guid assignmentId, [FromBody] UpdateAssignmentRequest req)
        {
            var uid = _current.GetUserId();
            var role = _current.GetRole();

            var courseId = await _db.TaskAssignments.AsNoTracking()
                .Where(a => a.Id == assignmentId)
                .Select(a => (Guid?)a.CourseId)
                .FirstOrDefaultAsync();
            if (courseId == null) return NotFound();
            if (!await _access.CanEditCourseAsync(uid, role, courseId.Value))
                return Forbid();

            await _assignments.UpdateAsync(assignmentId, uid, req);
            return NoContent();
        }

        [HttpDelete("assignments/{assignmentId:guid}")]
        public async Task<IActionResult> Delete([FromRoute] Guid assignmentId)
        {
            var uid = _current.GetUserId();
            var role = _current.GetRole();

            var courseId = await _db.TaskAssignments.AsNoTracking()
                .Where(a => a.Id == assignmentId)
                .Select(a => (Guid?)a.CourseId)
                .FirstOrDefaultAsync();
            if (courseId == null) return NotFound();
            if (!await _access.CanEditCourseAsync(uid, role, courseId.Value))
                return Forbid();

            await _assignments.DeleteAsync(assignmentId, uid);
            return NoContent();
        }


        [HttpPatch("assignments/{assignmentId:guid}/visibility")]
        public async Task<IActionResult> UpdateVisibility([FromRoute] Guid assignmentId, [FromBody] UpdateAssignmentVisibilityRequest body)
        {
            var uid = _current.GetUserId();
            var role = _current.GetRole();

            var task = await _db.TaskAssignments.FirstOrDefaultAsync(a => a.Id == assignmentId);
            if (task == null) return NotFound();
            if (!await _access.CanEditCourseAsync(uid, role, task.CourseId))
                return Forbid();

            var status = (body.LifecycleStatus ?? (body.IsHidden ? "draft" : "published")).Trim().ToLowerInvariant();
            status = status switch
            {
                "draft" => "draft",
                "polishing" => "polishing",
                "ready" => "ready",
                "published" => "published",
                "archived" => "archived",
                _ => body.IsHidden ? "draft" : "published"
            };

            task.IsHidden = body.IsHidden;
            task.LifecycleStatus = status;
            task.PublishedAtUtc = body.IsHidden ? null : (task.PublishedAtUtc ?? DateTime.UtcNow);
            task.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            return Ok(new { task.Id, task.IsHidden, task.LifecycleStatus, task.PublishedAtUtc });
        }

        [HttpPatch("assignments/{assignmentId:guid}/sort")]
        public async Task<IActionResult> UpdateSort([FromRoute] Guid assignmentId, [FromBody] UpdateAssignmentSortRequest body)
        {
            var uid = _current.GetUserId();
            var role = _current.GetRole();

            var courseId = await _db.TaskAssignments.AsNoTracking()
                .Where(a => a.Id == assignmentId)
                .Select(a => (Guid?)a.CourseId)
                .FirstOrDefaultAsync();
            if (courseId == null) return NotFound();
            if (!await _access.CanEditCourseAsync(uid, role, courseId.Value))
                return Forbid();

            await _assignments.UpdateSortAsync(assignmentId, uid, body.Sort);
            return NoContent();
        }

        [HttpPatch("assignments/{assignmentId:guid}/position")]
        public async Task<IActionResult> MoveAssignment([FromRoute] Guid assignmentId, [FromBody] MoveAssignmentRequest body)
        {
            var uid = _current.GetUserId();
            var role = _current.GetRole();

            var courseId = await _db.TaskAssignments.AsNoTracking()
                .Where(a => a.Id == assignmentId)
                .Select(a => (Guid?)a.CourseId)
                .FirstOrDefaultAsync();
            if (courseId == null) return NotFound();
            if (!await _access.CanEditCourseAsync(uid, role, courseId.Value))
                return Forbid();

            var ok = await _assignments.PlaceAfterAssignmentAsync(assignmentId, body.AfterAssignmentId, uid);
            if (!ok) return BadRequest(new { message = "Некорректная позиция задания" });
            return NoContent();
        }


    }
}
