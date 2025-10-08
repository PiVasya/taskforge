using Microsoft.AspNetCore.Mvc;
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

    public sealed class RunRequest { public string? code { get; set; } public string? input { get; set; } }
    public sealed class RunResponse
    {
        public string? stdout { get; set; }
        public string? stderr { get; set; }
        public int exitCode { get; set; }
        public string? error { get; set; } // компиляционные ошибки сюда тоже
    }

    [HttpPost("/run")]
    public ActionResult<RunResponse> Run([FromBody] RunRequest req)
    {
        var (ok, asm, err) = _compiler.Compile(req.code ?? "");
        if (!ok || asm == null)
        {
            return Ok(new RunResponse
            {
                stdout = null,
                stderr = err,   // дублируем в stderr
                exitCode = 1,
                error = err
            });
        }

        var (okRun, stdout, runErr) = _exec.Run(asm, req.input ?? "", TimeSpan.FromSeconds(2));
        return Ok(new RunResponse
        {
            stdout = okRun ? stdout : null,
            stderr = okRun ? null : runErr,
            exitCode = okRun ? 0 : 1,
            error = okRun ? null : runErr
        });
    }

    public sealed class TestItem { public string? input { get; set; } public string? expectedOutput { get; set; } }
    public sealed class TestsRequest { public string? code { get; set; } public List<TestItem>? tests { get; set; } }
    public sealed class TestsResponse { public List<object> results { get; set; } = new(); }

    [HttpPost("/run/tests")]
    public ActionResult<TestsResponse> RunTests([FromBody] TestsRequest req)
    {
        var (ok, asm, err) = _compiler.Compile(req.code ?? "");
        if (!ok || asm == null)
        {
            // Пусть клиент покажет compile error и не продолжает
            return Ok(new TestsResponse { results = new List<object>() });
        }

        var res = new TestsResponse();
        foreach (var t in (req.tests ?? new List<TestItem>()))
        {
            var (okRun, stdout, runErr) = _exec.Run(asm, t.input ?? "", TimeSpan.FromSeconds(2));
            var actual = okRun ? stdout : "";
            res.results.Add(new
            {
                input = t.input ?? "",
                expectedOutput = t.expectedOutput ?? "",
                actualOutput = actual,
                passed = okRun && string.Equals(
                    (t.expectedOutput ?? "").Replace("\r\n","\n").TrimEnd(),
                    (actual ?? "").Replace("\r\n","\n").TrimEnd(),
                    StringComparison.Ordinal)
            });
        }
        return Ok(res);
    }
}
