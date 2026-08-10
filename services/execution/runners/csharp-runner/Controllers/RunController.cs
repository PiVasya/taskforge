using Microsoft.AspNetCore.Mvc;
using Runner.Models;
using Runner.Services;

namespace Runner.Controllers;

[ApiController]
[Route("[controller]")]
[RequestSizeLimit(RunnerLimits.MaxRequestBytes)]
public sealed class RunController : ControllerBase
{
    private static readonly TimeSpan CompilationTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan MaxBatchDuration = TimeSpan.FromSeconds(40);

    private readonly IRoslynCompilationService _compiler;
    private readonly IExecutionService _execution;
    private readonly RunnerJobGate _jobGate;
    private readonly PolicyAttestationVerifier _attestationVerifier;
    private readonly ILogger<RunController> _logger;

    public RunController(
        IRoslynCompilationService compiler,
        IExecutionService execution,
        RunnerJobGate jobGate,
        PolicyAttestationVerifier attestationVerifier,
        ILogger<RunController> logger)
    {
        _compiler = compiler;
        _execution = execution;
        _jobGate = jobGate;
        _attestationVerifier = attestationVerifier;
        _logger = logger;
    }

    [HttpPost("run")]
    [HttpPost("/run")]
    public async Task<ActionResult<RunResponse>> Run([FromBody] RunRequest request)
    {
        var validationError = request is null ? "Request is empty." : RunnerLimits.Validate(request);
        if (validationError is not null)
        {
            return BadRequest(new RunResponse
            {
                Status = "bad_request",
                ExitCode = 1,
                Error = validationError
            });
        }

        var attestationError = _attestationVerifier.Verify("csharp", "standard", request.Code, request.Attestation);
        if (attestationError is not null)
        {
            _logger.LogWarning("C# runner rejected unattested source: {Reason}", attestationError);
            return StatusCode(StatusCodes.Status403Forbidden, new RunResponse
            {
                Status = "policy_error",
                ExitCode = 126,
                Error = "Решение не прошло обязательную проверку безопасности."
            });
        }

        var entered = false;
        try
        {
            await _jobGate.EnterAsync(HttpContext.RequestAborted);
            entered = true;

            RoslynCompilationResult compileResult;
            using (var compilationCts = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted))
            {
                compilationCts.CancelAfter(CompilationTimeout);
                try
                {
                    compileResult = _compiler.Compile(request.Code, compilationCts.Token);
                }
                catch (OperationCanceledException) when (!HttpContext.RequestAborted.IsCancellationRequested)
                {
                    return Ok(new RunResponse
                    {
                        Status = "time_limit",
                        Stdout = "",
                        Stderr = "Compilation time limit exceeded.",
                        ExitCode = 124,
                        Error = "Compilation time limit exceeded."
                    });
                }
            }

            if (!compileResult.Ok || compileResult.Pe is null)
            {
                var policyFailure = compileResult.FailureKind == CompilationFailureKind.PolicyError;
                return Ok(new RunResponse
                {
                    Status = policyFailure ? "policy_error" : "compile_error",
                    Stdout = "",
                    Stderr = "",
                    ExitCode = policyFailure ? 126 : 1,
                    Error = SanitizeRunnerText(compileResult.Error)
                });
            }

            var input = string.IsNullOrEmpty(request.Input) ? "\n" : request.Input;
            var (ran, stdout, error, status) = await _execution.RunAsync(
                compileResult.Pe,
                compileResult.Pdb ?? [],
                input,
                RunnerLimits.TimeLimitMs(request.TimeLimitMs),
                RunnerLimits.MemoryLimitMb(request.MemoryLimitMb),
                HttpContext.RequestAborted);

            return Ok(new RunResponse
            {
                Status = status,
                Stdout = stdout,
                Stderr = ran ? "" : FriendlyRunnerError(error),
                ExitCode = ran ? 0 : status == "policy_error" ? 126 : status == "time_limit" ? 124 : 1,
                Error = ran ? "" : FriendlyRunnerError(error)
            });
        }
        finally
        {
            if (entered)
            {
                _jobGate.Exit();
            }
        }
    }

    [HttpPost("tests")]
    [HttpPost("/run/tests")]
    [HttpPost("/run-tests")]
    public async Task<ActionResult<TestResultsResponse>> RunTests([FromBody] RunRequestWithTests request)
    {
        var validationError = request is null ? "Request is empty." : RunnerLimits.Validate(request);
        if (validationError is not null)
        {
            return BadRequest(new { error = validationError });
        }

        var attestationError = _attestationVerifier.Verify("csharp", "standard", request.Code, request.Attestation);
        if (attestationError is not null)
        {
            _logger.LogWarning("C# runner rejected unattested source: {Reason}", attestationError);
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                status = "policy_error",
                message = "Решение не прошло обязательную проверку безопасности."
            });
        }

        var entered = false;
        try
        {
            await _jobGate.EnterAsync(HttpContext.RequestAborted);
            entered = true;

            var tests = request.Tests ?? [];
            RoslynCompilationResult compileResult;
            using (var compilationCts = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted))
            {
                compilationCts.CancelAfter(CompilationTimeout);
                try
                {
                    compileResult = _compiler.Compile(request.Code, compilationCts.Token);
                }
                catch (OperationCanceledException) when (!HttpContext.RequestAborted.IsCancellationRequested)
                {
                    var first = tests.FirstOrDefault();
                    return Ok(new TestResultsResponse
                    {
                        Results =
                        [
                            new TestResult
                            {
                                Input = first?.Input ?? "",
                                ExpectedOutput = first?.ExpectedOutput ?? "",
                                ActualOutput = "",
                                Passed = false,
                                Status = "time_limit",
                                ExitCode = 124,
                                Stderr = "Compilation time limit exceeded.",
                                CompileStderr = "Compilation time limit exceeded.",
                                Hidden = first?.IsHidden ?? false
                            }
                        ]
                    });
                }
            }

            if (!compileResult.Ok || compileResult.Pe is null)
            {
                var first = tests.FirstOrDefault();
                var policyFailure = compileResult.FailureKind == CompilationFailureKind.PolicyError;
                return Ok(new TestResultsResponse
                {
                    Results =
                    [
                        new TestResult
                        {
                            Input = first?.Input ?? "",
                            ExpectedOutput = first?.ExpectedOutput ?? "",
                            ActualOutput = "",
                            Passed = false,
                            Status = policyFailure ? "policy_error" : "compile_error",
                            ExitCode = policyFailure ? 126 : 1,
                            Stderr = policyFailure ? "Решение отклонено системой безопасности." : "",
                            CompileStderr = policyFailure ? null : SanitizeRunnerText(compileResult.Error),
                            Hidden = first?.IsHidden ?? false
                        }
                    ]
                });
            }

            var results = new List<TestResult>(tests.Count);
            var timeLimitMs = RunnerLimits.TimeLimitMs(request.TimeLimitMs);
            var memoryLimitMb = RunnerLimits.MemoryLimitMb(request.MemoryLimitMb);

            using var batchCts = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted);
            batchCts.CancelAfter(MaxBatchDuration);

            foreach (var test in tests)
            {
                HttpContext.RequestAborted.ThrowIfCancellationRequested();
                if (batchCts.IsCancellationRequested)
                {
                    results.Add(BatchTimeoutResult(test));
                    break;
                }

                var input = string.IsNullOrEmpty(test.Input) ? "\n" : test.Input;
                (bool ran, string stdout, string error, string status) runResult;
                try
                {
                    runResult = await _execution.RunAsync(
                        compileResult.Pe,
                        compileResult.Pdb ?? [],
                        input,
                        timeLimitMs,
                        memoryLimitMb,
                        batchCts.Token);
                }
                catch (OperationCanceledException) when (!HttpContext.RequestAborted.IsCancellationRequested)
                {
                    results.Add(BatchTimeoutResult(test));
                    break;
                }

                var (ran, stdout, error, status) = runResult;
                var actual = stdout ?? "";
                var passed = ran && string.Equals(
                    NormalizeOutput(test.ExpectedOutput ?? ""),
                    NormalizeOutput(actual),
                    StringComparison.Ordinal);

                if (!ran && string.IsNullOrEmpty(actual))
                {
                    actual = FriendlyRunnerError(error);
                }

                results.Add(new TestResult
                {
                    Input = test.Input ?? "",
                    ExpectedOutput = test.ExpectedOutput ?? "",
                    ActualOutput = actual,
                    Passed = passed,
                    Status = passed ? "ok" : ran ? "wrong_answer" : status,
                    ExitCode = ran ? 0 : status == "policy_error" ? 126 : status == "time_limit" ? 124 : 1,
                    Stderr = ran ? "" : FriendlyRunnerError(error),
                    CompileStderr = null,
                    Hidden = test.IsHidden
                });

                if (status == "policy_error" || batchCts.IsCancellationRequested)
                {
                    break;
                }
            }

            return Ok(new TestResultsResponse { Results = results });
        }
        finally
        {
            if (entered)
            {
                _jobGate.Exit();
            }
        }
    }

    private static TestResult BatchTimeoutResult(TestCase test)
        => new()
        {
            Input = test.Input ?? "",
            ExpectedOutput = test.ExpectedOutput ?? "",
            ActualOutput = "",
            Passed = false,
            Status = "time_limit",
            ExitCode = 124,
            Stderr = "Batch time limit exceeded.",
            CompileStderr = null,
            Hidden = test.IsHidden
        };

    private static string NormalizeOutput(string value)
        => value.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd();

    private static string FriendlyRunnerError(string? value)
    {
        var text = value ?? "";
        var lower = text.ToLowerInvariant();
        if (lower.Contains("fork/exec", StringComparison.Ordinal) && lower.Contains("permission denied", StringComparison.Ordinal))
        {
            return "Не удалось запустить программу: нет прав на выполнение файла проверки.";
        }
        return SanitizeRunnerText(text);
    }

    private static string SanitizeRunnerText(string? value)
    {
        var text = value ?? "";
        if (string.IsNullOrWhiteSpace(text))
        {
            return text;
        }
        text = System.Text.RegularExpressions.Regex.Replace(text, @"/tmp/taskforge-[^\s:]+", "[временный файл]");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"/tmp/go-build[^\s:]+", "[временный файл]");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"/app/[^\s:]+", "[внутренний файл]");
        return text.Length <= 64 * 1024 ? text : text[..(64 * 1024)];
    }
}
