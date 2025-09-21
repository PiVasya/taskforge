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
            var list = await _courses.GetListAsync(_current.GetUserId());
            return Ok(list);
        }

        // GET /api/courses/{courseId} — карточка курса
        [HttpGet("{courseId:guid}")]
        public async Task<IActionResult> GetById([FromRoute] Guid courseId)
        {
            // Если у тебя в сервисе метод называется GetDetailsAsync — раскомментируй строку ниже и закомментируй текущую.
            // var dto = await _courses.GetDetailsAsync(courseId, _current.GetUserId());

            var dto = await _courses.GetDetailsAsync(courseId, _current.GetUserId());
            if (dto == null) return NotFound();
            return Ok(dto);
        }

        // PUT /api/courses/{courseId} — редактирование курса
        [HttpPut("{courseId:guid}")]
        public async Task<IActionResult> Update([FromRoute] Guid courseId, [FromBody] UpdateCourseRequest req)
        {
            await _courses.UpdateAsync(courseId, _current.GetUserId(), req);
            return NoContent();
        }

        // DELETE /api/courses/{courseId} — удаление курса (опционально)
        [HttpDelete("{courseId:guid}")]
        public async Task<IActionResult> Delete([FromRoute] Guid courseId)
        {
            await _courses.DeleteAsync(courseId, _current.GetUserId());
            return NoContent();
        }
    }
}
