using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Logging;
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
        private readonly ILogger<CompilerService> _logger;

        public CompilerService(ILogger<CompilerService> logger)
        {
            _logger = logger;
        }

        public async Task<CompilerRunResponseDto> CompileAndRunAsync(CompilerRunRequestDto request)
        {
            _logger.LogInformation("CompileAndRunAsync invoked with language={Language}", request.Language);

            return request.Language.ToLowerInvariant() switch
            {
                "csharp" or "c#" => await RunCSharpAsync(request.Code, request.Input),
                "cpp" or "c++" => await RunCppAsync(request.Code, request.Input),
                _ => new CompilerRunResponseDto { Error = $"Unsupported language: {request.Language}" }
            };
        }

        public async Task<IList<TestResultDto>> RunTestsAsync(TestRunRequestDto request)
        {
            if (request.TestCases is null || request.TestCases.Count == 0)
            {
                _logger.LogWarning("RunTestsAsync invoked with no test cases");
                return new List<TestResultDto>();
            }

            _logger.LogInformation("RunTestsAsync invoked with language={Language} and {Count} test cases",
                request.Language, request.TestCases.Count);

            return request.Language.ToLowerInvariant() switch
            {
                "csharp" or "c#" => await RunCSharpTestsAsync(request.Code, request.TestCases),
                "cpp" or "c++" => await RunCppTestsAsync(request.Code, request.TestCases),
                _ => throw new NotSupportedException($"Unsupported language: {request.Language}")
            };
        }

        private async Task<CompilerRunResponseDto> RunCSharpAsync(string code, string? input)
        {
            _logger.LogDebug("Compiling C# code, length={Length}", code?.Length ?? 0);

            var compilationResult = CompileCSharp(code);
            if (!compilationResult.Success)
            {
                _logger.LogError("C# compilation failed: {Error}", compilationResult.Error);
                return new CompilerRunResponseDto { Error = compilationResult.Error };
            }

            _logger.LogDebug("Compilation succeeded, executing assembly...");
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
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception occurred while executing compiled C# code");
                return new CompilerRunResponseDto { Error = ex.Message };
            }
            finally
            {
                Console.SetIn(originalIn);
                Console.SetOut(originalOut);
            }
            var output = outputWriter.ToString();
            _logger.LogDebug("Execution complete, output length={Length}", output.Length);
            return new CompilerRunResponseDto { Output = output };
        }

        private async Task<IList<TestResultDto>> RunCSharpTestsAsync(string code, IList<TestCaseDto> testCases)
        {
            _logger.LogDebug("Compiling C# code for tests");
            var compilationResult = CompileCSharp(code);
            if (!compilationResult.Success)
            {
                _logger.LogError("C# compilation failed for tests: {Error}", compilationResult.Error);
                throw new Exception(compilationResult.Error);
            }

            var entry = compilationResult.Assembly!.EntryPoint!;
            var results = new List<TestResultDto>();

            foreach (var test in testCases)
            {
                _logger.LogTrace("Running test with input: {Input}", test.Input);
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
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Exception while running test input: {Input}", test.Input);
                }
                finally
                {
                    Console.SetIn(originalIn);
                    Console.SetOut(originalOut);
                }
                var actual = outputWriter.ToString().Trim();
                var passed = actual == (test.ExpectedOutput?.Trim() ?? "");
                _logger.LogTrace("Test result: expected={Expected}, actual={Actual}, passed={Passed}",
                    test.ExpectedOutput, actual, passed);
                results.Add(new TestResultDto
                {
                    Input = test.Input,
                    ExpectedOutput = test.ExpectedOutput,
                    ActualOutput = actual,
                    Passed = passed
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

        private async Task<CompilerRunResponseDto> RunCppAsync(string code, string? input)
        {
            _logger.LogInformation("RunCppAsync called, but C++ support is not yet implemented");
            throw new NotImplementedException("C++ execution not implemented.");
        }

        private async Task<IList<TestResultDto>> RunCppTestsAsync(string code, IList<TestCaseDto> testCases)
        {
            _logger.LogInformation("RunCppTestsAsync called, but C++ support is not yet implemented");
            throw new NotImplementedException("C++ test execution not implemented.");
        }
    }
}
