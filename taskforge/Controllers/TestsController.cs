// Controllers/TestsController.cs
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Threading.Tasks;
using taskforge.Data.Models.DTO;
using taskforge.Services.Interfaces;

namespace taskforge.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class TestsController : ControllerBase
    {
        private readonly ICompilerService _service;

        public TestsController(ICompilerService service)
        {
            _service = service;
        }

        [HttpPost("run/tests")]
        public async Task<IActionResult> RunTests([FromBody] TestRunRequestDto request)
        {
            // проверка входных данных
            if (request.TestCases == null || request.TestCases.Count == 0)
            {
                return BadRequest(new { error = "No test cases provided." });
            }

            var results = await _service.RunTestsAsync(request);
            return Ok(new { results });
        }
    }
}
