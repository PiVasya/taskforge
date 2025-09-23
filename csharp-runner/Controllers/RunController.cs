using Microsoft.AspNetCore.Mvc;
using Runner.Models;
using Runner.Services;

namespace Runner.Controllers;

[ApiController]
[Route("/")]
public class RunController : ControllerBase
{
    private static readonly TimeSpan RunTimeout = TimeSpan.FromSeconds(5);
    private readonly ICompilationService _compiler;
    private readonly IExecutionService _exec;

    public RunController(ICompilationService compiler, IExecutionService exec)
    {
        _compiler = compiler; _exec = exec;
    }

    [HttpPost("run")]
    public ActionResult<RunResponse> Run([FromBody] RunRequest req)
    {
        var (ok, asm, err) = _compiler.CompileToAssembly(req.Code);
        if (!ok) return new RunResponse { Error = err, ExitCode = 1 };

        var (ran, stdout, ex) = _exec.Run(asm!, req.Input ?? "", RunTimeout);
        return ran
            ? new RunResponse { Stdout = stdout, Stderr = "", ExitCode = 0 }
            : new RunResponse { Stdout = "", Stderr = ex, ExitCode = 1 };
    }

    [HttpPost("run/tests")]
    public ActionResult<TestResultsResponse> RunTests([FromBody] RunRequestWithTests req)
    {
        var (ok, asm, err) = _compiler.CompileToAssembly(req.Code);
        if (!ok)
        {
            return new TestResultsResponse
            {
                Results = new[] {
                new TestResult { ActualOutput = err, Passed = false }
            }
            };
        }

        var list = new List<TestResult>(req.Tests?.Count ?? 0);
        foreach (var t in req.Tests ?? Enumerable.Empty<TestCase>())
        {
            var (ran, stdout, ex) = _exec.Run(asm!, t.Input ?? "", RunTimeout);
            list.Add(new TestResult
            {
                Input = t.Input,
                ExpectedOutput = t.ExpectedOutput,
                ActualOutput = ran ? stdout : ex,
                Passed = ran && stdout == (t.ExpectedOutput ?? "")
            });
        }
        return new TestResultsResponse { Results = list };
    }

    public sealed class RunRequestWithTests : RunRequest
    {
        public List<TestCase>? Tests { get; init; }
    }
}
