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

        public Task<IList<TestResultDto>> RunTestsAsync(TestRunRequestDto req)
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

            return compiler.RunTestsAsync(
                req.Code,
                req.TestCases ?? new List<TestCaseDto>(),
                req.TimeLimitMs,
                req.MemoryLimitMb
            );
        }
    }
}
