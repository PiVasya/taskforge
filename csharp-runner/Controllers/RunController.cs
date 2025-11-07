using Microsoft.AspNetCore.Mvc;
using Runner.Models;
using Runner.Services;
using System;

namespace Runner.Controllers;

[ApiController]
[Route("[controller]")]
public sealed class RunController : ControllerBase
{
    private static readonly TimeSpan RunTimeout = TimeSpan.FromSeconds(3);

    private readonly IRoslynCompilationService _compiler;
    private readonly IExecutionService _exec;

    public RunController(IRoslynCompilationService compiler, IExecutionService exec)
    {
        _compiler = compiler;
        _exec = exec;
    }

    // =====================================================================
    // POST /run/run  — одиночный запуск (для отдельного онлайн-компилятора)
    // =====================================================================
    [HttpPost("run")]
    public ActionResult<RunResponse> Run([FromBody] RunRequest req)
    {
        if (req is null || string.IsNullOrWhiteSpace(req.Code))
            return Ok(new RunResponse { ExitCode = 1, Error = "Code is empty." });

        var (ok, asm, compileErr) = _compiler.Compile(req.Code);
        if (!ok || asm is null)
        {
            return Ok(new RunResponse
            {
                Stdout = "",
                Stderr = "",
                ExitCode = 1,
                Error = compileErr ?? "Compilation error"
            });
        }

        var (ranOk, stdout, err) = _exec.Run(asm, req.Input ?? "", RunTimeout);

        return Ok(new RunResponse
        {
            Stdout = stdout,
            Stderr = ranOk ? "" : (err ?? ""),
            ExitCode = ranOk ? 0 : 1,
            Error = ranOk ? "" : (err ?? "Runtime error")
        });
    }

    // =====================================================================
    // POST /run/tests  — прогон тестов
    // Поддерживает fast-режим: остановиться на первом фейле и отдать детали.
    // =====================================================================
    [HttpPost("tests")]
    public ActionResult<TestResultsResponse> RunTests([FromBody] RunRequestWithTests req)
    {
        if (req is null || string.IsNullOrWhiteSpace(req.Code))
            return Ok(new TestResultsResponse
            {
                CompileError = "Code is empty.",
                Results = new()
            });

        var (ok, asm, compileErr) = _compiler.Compile(req.Code);
        if (!ok || asm is null)
        {
            return Ok(new TestResultsResponse
            {
                CompileError = compileErr ?? "Compilation error",
                Results = new()
            });
        }

        var results = new List<TestResult>();
        TestResult? firstFail = null;

        var tests = req.Tests ?? new List<TestCase>();
        for (int i = 0; i < tests.Count; i++)
        {
            var t = tests[i];

            var input = t.Input ?? "";
            var (ranOk, stdout, ex) = _exec.Run(asm, input, RunTimeout);

            // Нормализуем текст для сравнения
            string exp = Normalize(t.ExpectedOutput ?? "");
            string act = Normalize(stdout ?? "");

            bool passed = ranOk && string.Equals(exp, act, StringComparison.Ordinal);

            // Что показать пользователю как фактический вывод:
            // если stdout пуст — показываем сообщение об ошибке/исключение
            string actualForClient = !string.IsNullOrEmpty(stdout) ? stdout! : (ex ?? "");

            var item = new TestResult
            {
                Name = string.IsNullOrWhiteSpace(t.Name) ? $"Test #{i + 1}" : t.Name!,
                Input = t.Input ?? "",
                ExpectedOutput = t.ExpectedOutput ?? "",
                ActualOutput = actualForClient,
                Passed = passed,
                ExitCode = ranOk ? 0 : 1,
                Stderr = ranOk ? "" : (ex ?? "")
            };

            results.Add(item);

            if (!passed && firstFail is null)
            {
                firstFail = item;
                if (req.Fast == true) // смок-режим — останавливаемся на первом фейле
                    break;
            }
        }

        return Ok(new TestResultsResponse
        {
            CompileError = "",
            FirstFail = firstFail,
            Results = results
        });
    }

    // ---- helpers ----------------------------------------------------------
    private static string Normalize(string s)
        => (s ?? string.Empty).Replace("\r\n", "\n").TrimEnd('\n');
}
