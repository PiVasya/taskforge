using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using taskforge.Data.Models.DTO;
using taskforge.Services.Interfaces;

namespace taskforge.Controllers
{
    [ApiController]
    [Route("api/courses")]
    [Authorize]
    public sealed class CoursesController : ControllerBase
    {
        private readonly ICourseService _courses;
        private readonly ICurrentUserService _current;

        public CoursesController(ICourseService courses, ICurrentUserService current)
        {
            _courses = courses;
            _current = current;
        }

        // POST /api/courses  — создание курса
        [HttpPost]
        public async Task<IActionResult> Create([FromBody] CreateCourseRequest req)
        {
            var id = await _courses.CreateAsync(req, _current.GetUserId());
            return CreatedAtAction(nameof(GetById), new { courseId = id }, new { id });
        }

        // GET /api/courses — список курсов текущего пользователя (или видимых ему)
        [HttpGet]
        public async Task<IActionResult> List()
        {
            var list = await _courses.GetListAsync(_current.GetUserId(), _current.IsAdmin());
            return Ok(list);
        }

        [HttpGet("{courseId:guid}")]
        public async Task<IActionResult> GetById([FromRoute] Guid courseId)
        {
            var dto = await _courses.GetDetailsAsync(courseId, _current.GetUserId(), _current.IsAdmin());
            if (dto == null) return NotFound();
            return Ok(dto);
        }

        [HttpPut("{courseId:guid}")]
        public async Task<IActionResult> Update([FromRoute] Guid courseId, [FromBody] UpdateCourseRequest req)
        {
            try
            {
                await _courses.UpdateAsync(courseId, _current.GetUserId(), _current.IsAdmin(), req);
                return NoContent();
            }
            catch (UnauthorizedAccessException ex)
            {
                return StatusCode(403, new { message = ex.Message });
            }
        }

        [HttpDelete("{courseId:guid}")]
        public async Task<IActionResult> Delete([FromRoute] Guid courseId)
        {
            try
            {
                await _courses.DeleteAsync(courseId, _current.GetUserId(), _current.IsAdmin());
                return NoContent();
            }
            catch (UnauthorizedAccessException ex)
            {
                return StatusCode(403, new { message = ex.Message });
            }
        }
    }
}
