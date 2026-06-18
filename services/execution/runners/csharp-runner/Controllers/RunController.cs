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

        var (ok, pe, pdb, compileErr) = _compiler.Compile(req.Code);
        if (!ok || pe is null)
        {
            return Ok(new RunResponse
            {
                Stdout = "",
                Stderr = "",
                ExitCode = 1,
                Error = SanitizeRunnerText(compileErr)
            });
        }

        var normalizedInput = string.IsNullOrEmpty(req.Input) ? "\n" : req.Input!;
        var (ranOk, stdout, err) = _exec.Run(pe, pdb ?? Array.Empty<byte>(), normalizedInput, Timeout(req.TimeLimitMs));

        return Ok(new RunResponse
        {
            Stdout = stdout,
            Stderr = "",
            ExitCode = ranOk ? 0 : 1,
            Error = ranOk ? "" : FriendlyRunnerError(err)
        });
    }

    // POST /run/tests
    [HttpPost("tests")]
    [HttpPost("/run/tests")]
    [HttpPost("/run-tests")]
    public ActionResult<TestResultsResponse> RunTests([FromBody] RunRequestWithTests req)
    {
        if (req is null || string.IsNullOrWhiteSpace(req.Code))
            return BadRequest();

        var (ok, pe, pdb, compileErr) = _compiler.Compile(req.Code);
        if (!ok || pe is null)
        {
            var first = req.Tests?.FirstOrDefault();
            return Ok(new TestResultsResponse
            {
                Results = new List<TestResult>
                {
                    ScrubHidden(new TestResult
                    {
                        Input = first?.Input ?? "",
                        ExpectedOutput = first?.ExpectedOutput ?? "",
                        ActualOutput = "",
                        Passed = false,
                        Status = "compile_error",
                        ExitCode = 1,
                        Stderr = "",
                        CompileStderr = SanitizeRunnerText(compileErr ?? ""),
                        Hidden = first?.IsHidden ?? false
                    })
                }
            });
        }

        var results = new List<TestResult>();
        var tests = req.Tests ?? new List<TestCase>();
        var timeout = Timeout(req.TimeLimitMs);

        foreach (var t in tests)
        {
            var input = t.Input ?? "";
            if (input.Length == 0) input = "\n";

            var (ranOk, stdout, ex) = _exec.Run(pe, pdb ?? Array.Empty<byte>(), input, timeout);

            string actual = stdout ?? "";
            bool passed = ranOk &&
                          string.Equals(
                              (t.ExpectedOutput ?? "").Replace("\r\n", "\n").TrimEnd(),
                              actual.Replace("\r\n", "\n").TrimEnd(),
                              StringComparison.Ordinal);

            if (!ranOk && string.IsNullOrEmpty(actual))
                actual = FriendlyRunnerError(ex ?? "");

            results.Add(ScrubHidden(new TestResult
            {
                Input = t.Input ?? "",
                ExpectedOutput = t.ExpectedOutput ?? "",
                ActualOutput = actual,
                Passed = passed,
                Status = passed ? "ok" : (ranOk ? "wrong_answer" : (string.Equals(ex, "Time limit exceeded.", StringComparison.OrdinalIgnoreCase) ? "time_limit" : "runtime_error")),
                ExitCode = ranOk ? 0 : 1,
                Stderr = ranOk ? "" : FriendlyRunnerError(ex ?? ""),
                CompileStderr = null,
                Hidden = t.IsHidden
            }));
        }

        return Ok(new TestResultsResponse { Results = results });
    }

    private static TimeSpan Timeout(int? timeLimitMs)
    {
        var ms = timeLimitMs.GetValueOrDefault(3000);
        ms = System.Math.Clamp(ms <= 0 ? 3000 : ms, 500, 30000);
        return TimeSpan.FromMilliseconds(ms + 1000);
    }

    private static TestResult ScrubHidden(TestResult result)
    {
        // The runner is an internal service. Keep the full result here and let
        // solutions-api remove hidden test details for non-editor users.
        return result;
    }

    private static string FriendlyRunnerError(string? value)
    {
        var s = value ?? "";
        var lower = s.ToLowerInvariant();
        if (lower.Contains("fork/exec") && lower.Contains("permission denied"))
            return "Не удалось запустить программу: нет прав на выполнение файла проверки.";
        return SanitizeRunnerText(s);
    }

    private static string SanitizeRunnerText(string? value)
    {
        var s = value ?? "";
        if (string.IsNullOrWhiteSpace(s)) return s;
        s = System.Text.RegularExpressions.Regex.Replace(s, @"/tmp/taskforge-[^\s:]+", "[временный файл]");
        s = System.Text.RegularExpressions.Regex.Replace(s, @"/tmp/go-build[^\s:]+", "[временный файл]");
        s = System.Text.RegularExpressions.Regex.Replace(s, @"/app/[^\s:]+", "[внутренний файл]");
        return s;
    }
}
