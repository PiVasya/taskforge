using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using taskforge.Data.Models.DTO;
using taskforge.Services.Interfaces;

namespace taskforge.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
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

        [HttpPost]
        public async Task<IActionResult> Create([FromBody] CreateCourseRequest req)
        {
            var id = await _courses.CreateAsync(req, _current.GetUserId());
            return CreatedAtAction(nameof(GetById), new { courseId = id }, new { id });
        }

        [HttpGet]
        public async Task<IActionResult> List()
        {
            var list = await _courses.GetListAsync(_current.GetUserId());
            return Ok(list);
        }

        [HttpGet("{courseId:guid}")]
        public async Task<IActionResult> GetById([FromRoute] Guid courseId)
        {
            var dto = await _courses.GetDetailsAsync(courseId, _current.GetUserId());
            if (dto == null) return NotFound();
            return Ok(dto);
        }
    }
}
