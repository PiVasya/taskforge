// Services/CompilerService.cs
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using taskforge.Data.Models.DTO;
using taskforge.Services.Interfaces;

namespace TaskForge.Services
{
    public class CompilerService : ICompilerService
    {
        public async Task<CompilerRunResponseDto> CompileAndRunAsync(CompilerRunRequestDto request)
        {
            return request.Language.ToLowerInvariant() switch
            {
                "csharp" or "c#" => await RunCSharpAsync(request.Code, request.Input),
                "cpp" or "c++" => await RunCppAsync(request.Code, request.Input),
                _ => new CompilerRunResponseDto { Error = "Unsupported language" }
            };
        }

        public async Task<IList<TestResultDto>> RunTestsAsync(TestRunRequestDto request)
        {
            if (request.TestCases is null || request.TestCases.Count == 0)
                return new List<TestResultDto>();

            return request.Language.ToLowerInvariant() switch
            {
                "csharp" or "c#" => await RunCSharpTestsAsync(request.Code, request.TestCases),
                "cpp" or "c++" => await RunCppTestsAsync(request.Code, request.TestCases),
                _ => throw new NotSupportedException("Unsupported language")
            };
        }

        private async Task<CompilerRunResponseDto> RunCSharpAsync(string code, string? input)
        {
            var compilationResult = CompileCSharp(code);
            if (!compilationResult.Success)
            {
                return new CompilerRunResponseDto { Error = compilationResult.Error };
            }

            // перенаправление ввода/вывода аналогично предыдущему примеру
            using var inputReader = new StringReader(input ?? "");
            using var outputWriter = new StringWriter();
            var originalIn = Console.In;
            var originalOut = Console.Out;
            Console.SetIn(inputReader);
            Console.SetOut(outputWriter);
            try
            {
                var entry = compilationResult.Assembly!.EntryPoint!;
                var result = entry.Invoke(null, entry.GetParameters().Length == 1 ? new object[] { Array.Empty<string>() } : null);
                if (result is Task task) await task;
            }
            finally
            {
                Console.SetIn(originalIn);
                Console.SetOut(originalOut);
            }
            return new CompilerRunResponseDto { Output = outputWriter.ToString() };
        }

        private async Task<IList<TestResultDto>> RunCSharpTestsAsync(string code, IList<TestCaseDto> testCases)
        {
            var compilationResult = CompileCSharp(code);
            if (!compilationResult.Success)
                throw new Exception(compilationResult.Error);

            var entry = compilationResult.Assembly!.EntryPoint!;
            var results = new List<TestResultDto>();
            foreach (var test in testCases)
            {
                using var inputReader = new StringReader(test.Input ?? "");
                using var outputWriter = new StringWriter();
                var originalIn = Console.In;
                var originalOut = Console.Out;
                Console.SetIn(inputReader);
                Console.SetOut(outputWriter);
                try
                {
                    var res = entry.Invoke(null, entry.GetParameters().Length == 1 ? new object[] { Array.Empty<string>() } : null);
                    if (res is Task task) await task;
                }
                finally
                {
                    Console.SetIn(originalIn);
                    Console.SetOut(originalOut);
                }
                var actual = outputWriter.ToString().Trim();
                results.Add(new TestResultDto
                {
                    Input = test.Input,
                    ExpectedOutput = test.ExpectedOutput,
                    ActualOutput = actual,
                    Passed = actual == (test.ExpectedOutput?.Trim() ?? "")
                });
            }
            return results;
        }

        private (bool Success, Assembly? Assembly, string? Error) CompileCSharp(string code)
        {
            var syntaxTree = CSharpSyntaxTree.ParseText(code);
            var references = new[]
            {
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(Console).Assembly.Location),
                MetadataReference.CreateFromFile(Assembly.Load("System.Runtime").Location),
            };
            var compilation = CSharpCompilation.Create(
                "UserProgram",
                new[] { syntaxTree },
                references,
                new CSharpCompilationOptions(OutputKind.ConsoleApplication));

            using var ms = new MemoryStream();
            var emitResult = compilation.Emit(ms);
            if (!emitResult.Success)
            {
                var errors = new StringBuilder();
                foreach (var diag in emitResult.Diagnostics)
                    errors.AppendLine(diag.ToString());
                return (false, null, errors.ToString());
            }
            ms.Seek(0, SeekOrigin.Begin);
            var assembly = Assembly.Load(ms.ToArray());
            return (true, assembly, null);
        }

        // методы RunCppAsync и RunCppTestsAsync аналогичны примерам выше: компиляция g++, запуск, сверка
        private async Task<CompilerRunResponseDto> RunCppAsync(string code, string? input)
        {
            // реализация аналогична предыдущему примеру
            // верните CompilerRunResponseDto с Output и Error
            throw new NotImplementedException();
        }

        private async Task<IList<TestResultDto>> RunCppTestsAsync(string code, IList<TestCaseDto> testCases)
        {
            // реализация аналогична предыдущему примеру
            throw new NotImplementedException();
        }
    }
}