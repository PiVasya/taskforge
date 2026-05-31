using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Linq;
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
            if (request?.TestCases == null || request.TestCases.Count == 0)
                return BadRequest(new { error = "No test cases provided." });

            // «сухая» компиляция
            CompilerRunResponseDto compileResp;
            try
            {
                compileResp = await _service.CompileAndRunAsync(new CompilerRunRequestDto
                {
                    Language = request.Language,
                    Code     = request.Code,
                    Input    = "" // без ввода
                });
            }
            catch (System.Exception ex)
            {
                return Ok(new
                {
                    status  = "infrastructure_error",
                    message = "Сервис компиляции недоступен",
                    compile = new { ok = false, stdout = "", stderr = ex.Message },
                    run     = (object)null,
                    tests   = new object[0],
                    rawResults = new object[0]
                });
            }

            var compileOk = compileResp != null && (compileResp.ExitCode == 0) && string.IsNullOrEmpty(compileResp.Stderr);
            var compileDto = new
            {
                ok     = compileOk,
                stdout = compileResp?.Stdout ?? "",
                stderr = compileResp?.Stderr ?? ""
            };

            if (!compileOk)
            {
                return Ok(new
                {
                    status  = "compile_error",
                    message = "Ошибка компиляции",
                    compile = compileDto,
                    run     = (object)null,
                    tests   = new object[0],
                    rawResults = new object[0]
                });
            }

            // реальные прогоны тестов
            var rawResults = await _service.RunTestsAsync(request);

            var testsForUi = rawResults.Select(r => new
            {
                input    = r.Input,
                expected = r.ExpectedOutput,
                actual   = r.ActualOutput,
                passed   = r.Passed,
                hidden   = r.Hidden,
                exitCode = r.ExitCode,
                stderr   = r.Stderr
            }).ToList();

            var passed = testsForUi.Count(t => t.passed);
            var failed = testsForUi.Count - passed;
            var status = failed == 0 ? "passed" : "failed_tests";

            return Ok(new
            {
                status,
                compile = compileDto,
                run     = (object)null,
                tests   = testsForUi,
                rawResults
            });
        }
    }
}
