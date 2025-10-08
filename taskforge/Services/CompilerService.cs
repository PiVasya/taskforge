﻿using System.ComponentModel.DataAnnotations;
using System.Text;
using taskforge.Data.Models.DTO;
using taskforge.Services.Interfaces;

namespace taskforge.Services
{
    public sealed class CompilerService : ICompilerService
    {
        private readonly ICompilerProvider _provider;

        public CompilerService(ICompilerProvider provider)
        {
            _provider = provider;
        }

        public async Task<CompilerRunResponseDto> CompileAndRunAsync(CompilerRunRequestDto req)
        {
            if (req == null) throw new ValidationException("Request body is required.");
            if (string.IsNullOrWhiteSpace(req.Language)) throw new ValidationException("Field 'language' is required.");
            if (string.IsNullOrWhiteSpace(req.Code)) throw new ValidationException("Field 'code' is required.");

            var compiler = _provider.GetCompiler(req.Language)
                ?? throw new ValidationException($"Unsupported language '{req.Language}'. Try: C++, C#, Python.");

            Console.WriteLine($"[CompileAndRun] lang={req.Language} TL={req.TimeLimitMs} ML={req.MemoryLimitMb}");
            var resp = await compiler.CompileAndRunAsync(req);
            Console.WriteLine($"[CompileAndRun] exit={resp?.ExitCode} lenOut={resp?.Stdout?.Length ?? 0} lenErr={resp?.Stderr?.Length ?? 0}");
            return resp;
        }

        /// <summary>
        /// Прогон тестов «по‑настоящему»: на каждый кейс вызываем CompileAndRunAsync,
        /// берём фактический stdout и сравниваем через канонизацию (CRLF/LF/хвостовые пробелы).
        /// </summary>
        public async Task<IList<TestResultDto>> RunTestsAsync(TestRunRequestDto req)
        {
            Console.WriteLine("[RunTests] >>> Start (robust path via CompileAndRunAsync)");

            if (req == null) throw new ValidationException("Request body is required.");
            if (string.IsNullOrWhiteSpace(req.Language)) throw new ValidationException("Field 'language' is required.");
            if (string.IsNullOrWhiteSpace(req.Code)) throw new ValidationException("Field 'code' is required.");

            var compiler = _provider.GetCompiler(req.Language)
                ?? throw new ValidationException($"Unsupported language '{req.Language}'. Try: C++, C#, Python.");

            var tests = req.TestCases ?? new List<TestCaseDto>();
            var results = new List<TestResultDto>(tests.Count);

            for (int i = 0; i < tests.Count; i++)
            {
                var tc = tests[i];
                Console.WriteLine($"[RunTests] --- test #{i + 1} ---");
                Console.WriteLine($"[RunTests] input.visible    : {ToVisible(tc.Input)}");
                Console.WriteLine($"[RunTests] expected.visible : {ToVisible(tc.ExpectedOutput)}  hex={ToHex(tc.ExpectedOutput)}");

                var run = await compiler.CompileAndRunAsync(new CompilerRunRequestDto
                {
                    Language      = req.Language,
                    Code          = req.Code,
                    Input         = tc.Input ?? string.Empty,
                    TimeLimitMs   = req.TimeLimitMs ?? 2000,
                    MemoryLimitMb = req.MemoryLimitMb ?? 256
                });

                var actual = run.Stdout ?? string.Empty;
                var exit   = run.ExitCode;
                Console.WriteLine($"[RunTests] exit={exit}  actual.visible: {ToVisible(actual)}  hex={ToHex(actual)}");

                bool passed = exit == 0 && string.Equals(Canon(tc.ExpectedOutput), Canon(actual), StringComparison.Ordinal);

                Console.WriteLine($"[RunTests] expected.canon  : {ToVisible(Canon(tc.ExpectedOutput))}");
                Console.WriteLine($"[RunTests] actual.canon    : {ToVisible(Canon(actual))}");
                Console.WriteLine($"[RunTests] PASSED={passed}");

                results.Add(new TestResultDto
                {
                    Input          = tc.Input ?? string.Empty,
                    ExpectedOutput = tc.ExpectedOutput ?? string.Empty,
                    ActualOutput   = actual,
                    Passed         = passed,
                    Status         = passed ? "ok" : (exit != 0 ? "runtime_error" : "ok"),
                    ExitCode       = exit,
                    Stderr         = run.Stderr ?? "",
                    CompileStderr  = run.CompileStderr ?? "",
                    Hidden         = tc.IsHidden
                });
            }

            Console.WriteLine("[RunTests] <<< Finish");
            return results;
        }

        // ===== helpers =====
        private static string Canon(string? s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            s = s.Replace("\r\n", "\n").Replace("\r", "\n");
            s = string.Join("\n", s.Split('\n').Select(line => line.TrimEnd(' ', '\t')));
            s = s.TrimEnd('\n');
            return s;
        }

        private static string ToVisible(string? s)
        {
            if (s == null) return "⟨null⟩";
            var sb = new StringBuilder(s.Length + 16);
            foreach (var ch in s)
                sb.Append(ch switch { '\r' => "\\r", '\n' => "\\n", '\t' => "\\t", _ => ch.ToString() });
            return sb.ToString();
        }

        private static string ToHex(string? s)
        {
            if (s == null) return "⟨null⟩";
            var bytes = Encoding.UTF8.GetBytes(s);
            return BitConverter.ToString(bytes);
        }
    }
}
