using Microsoft.AspNetCore.Mvc;
using Runner.Models;
using Runner.Services;

namespace Runner.Controllers;

[ApiController]
[Route("[controller]")]
public sealed class RunController : ControllerBase
{
    private readonly IRoslynCompilationService _compiler;
    private readonly IExecutionService _exec;

    public RunController(IRoslynCompilationService compiler, IExecutionService exec)
    {
        _compiler = compiler;
        _exec = exec;
    }

    // POST /run/run
    [HttpPost("run")]
    [HttpPost("/run")]
    public ActionResult<RunResponse> Run([FromBody] RunRequest req)
    {
        if (req is null || string.IsNullOrWhiteSpace(req.Code))
            return BadRequest(new RunResponse { Error = "Code is empty." });

        var (ok, asm, compileErr) = _compiler.Compile(req.Code);
        if (!ok || asm is null)
        {
            return Ok(new RunResponse
            {
                Stdout = "",
                Stderr = "",
                ExitCode = 1,
                Error = compileErr
            });
        }

        // при пустом вводе отправляем хотя бы перевод строки, чтобы ReadLine() не вернул null
        var normalizedInput = string.IsNullOrEmpty(req.Input) ? "\n" : req.Input!;
        var (ranOk, stdout, err) = _exec.Run(asm, normalizedInput, TimeSpan.FromSeconds(3));

        return Ok(new RunResponse
        {
            Stdout = stdout,
            Stderr = "",
            ExitCode = ranOk ? 0 : 1,
            Error = ranOk ? "" : err
        });
    }

    // POST /run/tests
    [HttpPost("tests")]
    [HttpPost("/run/tests")]
    public ActionResult<TestResultsResponse> RunTests([FromBody] RunRequestWithTests req)
    {
        if (req is null || string.IsNullOrWhiteSpace(req.Code))
            return BadRequest();

        var (ok, asm, compileErr) = _compiler.Compile(req.Code);
        if (!ok || asm is null)
        {
            // Возвращаем одну «ошибочную» запись, чтобы UI отобразил ошибку компиляции
            return Ok(new TestResultsResponse
            {
                Results = new List<TestResult>
                {
                    new TestResult
                    {
                        Input = "",
                        ExpectedOutput = "",
                        ActualOutput = compileErr ?? "",
                        Passed = false
                    }
                }
            });
        }

        var results = new List<TestResult>();
        var tests = req.Tests ?? new List<TestCase>();

        foreach (var t in tests)
        {
            var input = t.Input ?? "";
            if (input.Length == 0) input = "\n"; // ключевая правка

            var (ranOk, stdout, ex) = _exec.Run(asm, input, TimeSpan.FromSeconds(3));

            string actual = stdout ?? "";
            // сравниваем без различий в типе переноса строки и без хвостовых переводов
            bool passed = ranOk &&
                          string.Equals(
                              (t.ExpectedOutput ?? "").Replace("\r\n", "\n").TrimEnd(),
                              (actual).Replace("\r\n", "\n").TrimEnd(),
                              StringComparison.Ordinal);

            if (!ranOk && string.IsNullOrEmpty(actual))
                actual = ex ?? "";

            results.Add(new TestResult
            {
                Input = t.Input ?? "",
                ExpectedOutput = t.ExpectedOutput ?? "",
                ActualOutput = actual,
                Passed = passed
            });
        }

        return Ok(new TestResultsResponse { Results = results });
    }
}
