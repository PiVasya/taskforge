using Microsoft.AspNetCore.Mvc;
using taskforge.Data.Models.DTO;
using taskforge.Services.Interfaces;   // << интерфейс!

namespace taskforge.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class CompilerController : ControllerBase
    {
        private readonly ICompilerService _svc;   // << интерфейс

        public CompilerController(ICompilerService svc)
        {
            _svc = svc;
        }

        [HttpPost("run")]
        public async Task<IActionResult> Run([FromBody] CompilerRunRequestDto req)
        {
            // на всякий — дефолт по языку, чтобы убрать nullable-warning
            req.Language ??= "cpp";

            var r = await _svc.CompileAndRunAsync(req);

            // если хочется — подстветим неудачную компиляцию 400-кой
            if (r.Status == "compile_error") return BadRequest(r);

            return Ok(r);
        }

        [HttpPost("run-tests")]
        public async Task<IActionResult> RunTests([FromBody] TestRunRequestDto req)
        {
            req.Language ??= "cpp";
            var list = await _svc.RunTestsAsync(req);
            return Ok(new { results = list });
        }
    }
}
