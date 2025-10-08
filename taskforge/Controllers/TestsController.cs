// Controllers/TestsController.cs
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

            Console.WriteLine($"[TestsController] run/tests lang={request.Language} code.len={request.Code?.Length ?? 0} tests={request.TestCases.Count}");

            // 1) «сухая» компиляция
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
                Console.WriteLine($"[TestsController] compile: EX {ex.GetType().Name} {ex.Message}");
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

            Console.WriteLine($"[TestsController] compile.ok={compileOk} exit={compileResp?.ExitCode}");

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

            // 2) реальные прогоны тестов
            var rawResults = await _service.RunTestsAsync(request);

            // 3) проекция под UI
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

            Console.WriteLine($"[TestsController] done: passed={passed} failed={failed}");

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
