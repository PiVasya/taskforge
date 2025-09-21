using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using taskforge.Data.Models.DTO;
using taskforge.Services.Interfaces;

namespace taskforge.Controllers
{
    [ApiController]
    [Route("api")]
    [Authorize]
    public sealed class AssignmentsController : ControllerBase
    {
        private readonly IAssignmentService _assignments;
        private readonly ICurrentUserService _current;

        public AssignmentsController(IAssignmentService assignments, ICurrentUserService current)
        {
            _assignments = assignments;
            _current = current;
        }

        [HttpPost("courses/{courseId:guid}/assignments")]
        public async Task<IActionResult> Create([FromRoute] Guid courseId, [FromBody] CreateAssignmentRequest req)
        {
            var id = await _assignments.CreateAsync(courseId, req, _current.GetUserId());
            return CreatedAtAction(nameof(GetById), new { assignmentId = id }, new { id });
        }

        [HttpGet("courses/{courseId:guid}/assignments")]
        public async Task<IActionResult> ListByCourse([FromRoute] Guid courseId)
        {
            var list = await _assignments.GetByCourseAsync(courseId, _current.GetUserId());
            return Ok(list);
        }

        [HttpGet("assignments/{assignmentId:guid}")]
        public async Task<IActionResult> GetById([FromRoute] Guid assignmentId)
        {
            var dto = await _assignments.GetDetailsAsync(assignmentId, _current.GetUserId());
            if (dto == null) return NotFound();
            return Ok(dto);
        }

        [HttpPut("assignments/{assignmentId:guid}")]
        public async Task<IActionResult> Update([FromRoute] Guid assignmentId, [FromBody] UpdateAssignmentRequest req)
        {
            await _assignments.UpdateAsync(assignmentId, _current.GetUserId(), req);
            return NoContent();
        }

        [HttpDelete("assignments/{assignmentId:guid}")]
        public async Task<IActionResult> Delete([FromRoute] Guid assignmentId)
        {
            await _assignments.DeleteAsync(assignmentId, _current.GetUserId());
            return NoContent();
        }
    }
}
