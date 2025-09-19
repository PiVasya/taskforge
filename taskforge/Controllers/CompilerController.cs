using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;
using taskforge.Data.Models.DTO;
using taskforge.Services.Interfaces;

namespace TaskForge.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class CompilerController : ControllerBase
    {
        private readonly ICompilerService _service;
        private readonly ILogger<CompilerController> _logger;

        public CompilerController(ICompilerService service, ILogger<CompilerController> logger)
        {
            _service = service;
            _logger = logger;
        }

        [HttpPost("compile-run")]
        public async Task<IActionResult> CompileRun([FromBody] CompilerRunRequestDto request)
        {
            _logger.LogInformation("Received CompileRun request: language={Language}, code length={CodeLength}, input length={InputLength}",
                request.Language, request.Code?.Length ?? 0, request.Input?.Length ?? 0);

            var result = await _service.CompileAndRunAsync(request);

            if (!string.IsNullOrWhiteSpace(result.Error))
            {
                _logger.LogWarning("CompileRun finished with error: {Error}", result.Error);
            }
            else
            {
                _logger.LogInformation("CompileRun finished successfully, output length={OutputLength}",
                    result.Output?.Length ?? 0);
            }

            return Ok(result);
        }

        [HttpPost("run-tests")]
        public async Task<IActionResult> RunTests([FromBody] TestRunRequestDto request)
        {
            _logger.LogInformation("Received RunTests request: language={Language}, code length={CodeLength}, test count={TestCount}",
                request.Language, request.Code?.Length ?? 0, request.TestCases?.Count ?? 0);

            var results = await _service.RunTestsAsync(request);

            _logger.LogInformation("RunTests finished. Total tests run: {Count}, passed={Passed}, failed={Failed}",
                results.Count,
                results.Count(t => t.Passed),
                results.Count(t => !t.Passed));

            return Ok(new { results });
        }
    }
}
