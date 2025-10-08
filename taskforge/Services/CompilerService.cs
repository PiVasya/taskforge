﻿using System.ComponentModel.DataAnnotations;
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
            if (req == null)
                throw new ValidationException("Request body is required.");

            if (string.IsNullOrWhiteSpace(req.Language))
                throw new ValidationException("Field 'language' is required.");

            if (string.IsNullOrWhiteSpace(req.Code))
                throw new ValidationException("Field 'code' is required.");

            var compiler = _provider.GetCompiler(req.Language);
            if (compiler is null)
                throw new ValidationException($"Unsupported language '{req.Language}'. Try: C++, C#, Python.");

            Console.WriteLine($"[CompileAndRun] lang={req.Language} TL={req.TimeLimitMs} ML={req.MemoryLimitMb}");
            var resp = await compiler.CompileAndRunAsync(req);
            Console.WriteLine($"[CompileAndRun] exit={resp?.ExitCode} lenOut={resp?.Stdout?.Length ?? 0} lenErr={resp?.Stderr?.Length ?? 0}");
            return resp;
        }

        public async Task<IList<TestResultDto>> RunTestsAsync(TestRunRequestDto req)
        {
            Console.WriteLine("[RunTests] >>> Start");
            if (req == null)
                throw new ValidationException("Request body is required.");

            if (string.IsNullOrWhiteSpace(req.Language))
                throw new ValidationException("Field 'language' is required.");

            if (string.IsNullOrWhiteSpace(req.Code))
                throw new ValidationException("Field 'code' is required.");

            var compiler = _provider.GetCompiler(req.Language);
            if (compiler is null)
                throw new ValidationException($"Unsupported language '{req.Language}'. Try: C++, C#, Python.");

            var results = await compiler.RunTestsAsync(
                req.Code,
                req.TestCases ?? new List<TestCaseDto>(),
                req.TimeLimitMs,
                req.MemoryLimitMb
            );

            Console.WriteLine($"[RunTests] got {results.Count} results from provider");
            int i = 0;

            foreach (var r in results)
            {
                i++;
                try
                {
                    var expected = (string?)r.GetType().GetProperty("Expected")?.GetValue(r)
                                   ?? (string?)r.GetType().GetProperty("ExpectedOutput")?.GetValue(r)
                                   ?? "";
                    var actual = (string?)r.GetType().GetProperty("Actual")?.GetValue(r)
                                 ?? (string?)r.GetType().GetProperty("Output")?.GetValue(r)
                                 ?? (string?)r.GetType().GetProperty("Stdout")?.GetValue(r)
                                 ?? "";

                    int exitCode = 0;
                    var exitCodeProp = r.GetType().GetProperty("ExitCode");
                    if (exitCodeProp != null && exitCodeProp.GetValue(r) is int ec) exitCode = ec;

                    var passedBefore = (bool?)r.GetType().GetProperty("Passed")?.GetValue(r);

                    Console.WriteLine($"[RunTests] #{i}: exit={exitCode} passed(before)={passedBefore}");
                    Console.WriteLine($"[RunTests] #{i}: expected.raw='{ToVisible(expected)}' (hex={ToHex(expected)})");
                    Console.WriteLine($"[RunTests] #{i}: actual.raw  ='{ToVisible(actual)}' (hex={ToHex(actual)})");

                    bool passed = string.Equals(Canon(expected), Canon(actual), StringComparison.Ordinal);
                    if (exitCode != 0) passed = false;

                    r.GetType().GetProperty("Passed")?.SetValue(r, passed);

                    Console.WriteLine($"[RunTests] #{i}: expected.canon='{ToVisible(Canon(expected))}'");
                    Console.WriteLine($"[RunTests] #{i}: actual.canon  ='{ToVisible(Canon(actual))}'");
                    Console.WriteLine($"[RunTests] #{i}: passed(after)={passed}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[RunTests] #{i}: normalize ERROR: {ex}");
                }
            }

            Console.WriteLine("[RunTests] <<< Finish");
            return results;

            static string Canon(string? s)
            {
                if (string.IsNullOrEmpty(s)) return string.Empty;
                s = s.Replace("\r\n", "\n").Replace("\r", "\n");
                s = string.Join("\n", s.Split('\n').Select(line => line.TrimEnd(' ', '\t')));
                s = s.TrimEnd('\n');
                return s;
            }

            static string ToVisible(string? s)
            {
                if (s == null) return "⟨null⟩";
                var sb = new System.Text.StringBuilder(s.Length + 16);
                foreach (var ch in s)
                {
                    sb.Append(ch switch
                    {
                        '\r' => "\\r",
                        '\n' => "\\n",
                        '\t' => "\\t",
                        _ => ch.ToString()
                    });
                }
                return sb.ToString();
            }

            static string ToHex(string? s)
            {
                if (s == null) return "⟨null⟩";
                var bytes = System.Text.Encoding.UTF8.GetBytes(s);
                return BitConverter.ToString(bytes);
            }
        }
    }
}
