using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using taskforge.Constants;
using taskforge.Filters;
using taskforge.Data;
using taskforge.Data.Models.DTO;
using taskforge.Services.Interfaces;

namespace taskforge.Controllers
{
    [ApiController]
    [Route("api/assignments/{assignmentId:guid}")]
    [Authorize]
    public sealed class SolutionsController : ControllerBase
    {
        private readonly ISolutionService _solutions;
        private readonly ICurrentUserService _current;
        private readonly ApplicationDbContext _db;
        private readonly ICourseAccessService _access;

        public SolutionsController(ISolutionService solutions, ICurrentUserService current, ApplicationDbContext db, ICourseAccessService access)
        {
            _solutions = solutions;
            _current = current;
            _db = db;
            _access = access;
        }

        [HttpGet("top-solutions")]
        [RequireQuota(QuotaBuckets.Top)]
        public async Task<IActionResult> GetTopSolutions([FromRoute] Guid assignmentId, [FromQuery] int top = 20)
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
                && !string.Equals(role, AppRoles.Admin, StringComparison.OrdinalIgnoreCase)
                && !await _access.CanEditCourseAsync(userId, role, assignmentAccess.CourseId))
                return NotFound();

            var list = await _solutions.GetTopSolutionsAsync(assignmentId, Math.Clamp(top, 1, 100));
            return Ok(list);
        }

        [HttpPost("submit")]
        [RequireQuota(QuotaBuckets.Tasks)]
        public async Task<IActionResult> Submit([FromRoute] Guid assignmentId, [FromBody] SubmitSolutionRequest req)
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
                && !string.Equals(role, AppRoles.Admin, StringComparison.OrdinalIgnoreCase)
                && !await _access.CanEditCourseAsync(userId, role, assignmentAccess.CourseId))
                return NotFound();

            var result = await _solutions.SubmitAsync(assignmentId, userId, req);
            return Ok(result);
        }
    }
}
