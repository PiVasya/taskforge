using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
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

        [HttpPost("submit")]
        public async Task<IActionResult> Submit([FromRoute] Guid assignmentId, [FromBody] SubmitSolutionRequest req)
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

            var result = await _solutions.SubmitAsync(assignmentId, userId, req);
            return Ok(result);
        }
    }
}
