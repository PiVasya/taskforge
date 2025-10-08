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

            // Пробрасываем DTO как есть: раннеры понимают TimeLimitMs/MemoryLimitMb.
            return await compiler.CompileAndRunAsync(req);
        }
        public async Task<IList<TestResultDto>> RunTestsAsync(TestRunRequestDto req)
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

            var results = await compiler.RunTestsAsync(
                req.Code,
                req.TestCases ?? new List<TestCaseDto>(),
                req.TimeLimitMs,
                req.MemoryLimitMb
            );

            // ---- Нормализация сравнения (чинит Python CRLF/LF) ----
            foreach (var r in results)
            {
                try
                {
                    // Пытаемся читать распространённые имена свойств
                    var expected = (string?)r.GetType().GetProperty("Expected")?.GetValue(r)
                                ?? (string?)r.GetType().GetProperty("ExpectedOutput")?.GetValue(r)
                                ?? "";
                    var actual = (string?)r.GetType().GetProperty("Actual")?.GetValue(r)
                                ?? (string?)r.GetType().GetProperty("Output")?.GetValue(r)
                                ?? (string?)r.GetType().GetProperty("Stdout")?.GetValue(r)
                                ?? "";

                    bool passed = string.Equals(Canon(expected), Canon(actual), StringComparison.Ordinal);

                    var exitCodeProp = r.GetType().GetProperty("ExitCode");
                    if (exitCodeProp != null && exitCodeProp.GetValue(r) is int ec && ec != 0)
                        passed = false;

                    var passedProp = r.GetType().GetProperty("Passed");
                    passedProp?.SetValue(r, passed);
                }
                catch
                {
                    // «best effort»: если структура незнакомая — не трогаем
                }
            }
            // -------------------------------------------------------

            return results;

            static string Canon(string? s)
            {
                if (string.IsNullOrEmpty(s)) return string.Empty;
                s = s.Replace("\r\n", "\n").Replace("\r", "\n");
                s = string.Join("\n", s.Split('\n').Select(line => line.TrimEnd(' ', '\t')));
                s = s.TrimEnd('\n');
                return s;
            }
        }
    }
}
