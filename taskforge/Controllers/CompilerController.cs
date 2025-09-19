// Controllers/CompilerController.cs
using Microsoft.AspNetCore.Mvc;
using System.Threading.Tasks;
using taskforge.Data.Models.DTO;
using taskforge.Services.Interfaces;

namespace TaskForge.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class CompilerController : ControllerBase
    {
        private readonly ICompilerService _service;
        public CompilerController(ICompilerService service)
        {
            _service = service;
        }

        [HttpPost("compile-run")]
        public async Task<IActionResult> CompileRun([FromBody] CompilerRunRequestDto request)
        {
            var result = await _service.CompileAndRunAsync(request);
            return Ok(result);
        }

        [HttpPost("run-tests")]
        public async Task<IActionResult> RunTests([FromBody] TestRunRequestDto request)
        {
            var results = await _service.RunTestsAsync(request);
            return Ok(new { results });
        }
    }
}
