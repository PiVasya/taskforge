using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System.Reflection;
using System.Text;
using taskforge.Data.Models.DTO;
using taskforge.Services.Interfaces;

namespace taskforge.Services.Compilers
{
    public class CSharpCompiler : ICompiler
    {
        public async Task<CompilerRunResponseDto> CompileAndRunAsync(string code, string? input)
        {
            var compilation = CompileCSharp(code);
            if (!compilation.Success)
                return new CompilerRunResponseDto { Error = compilation.Error };

            using var inputReader = new StringReader(input ?? "");
            using var outputWriter = new StringWriter();
            var originalIn = Console.In;
            var originalOut = Console.Out;

            Console.SetIn(inputReader);
            Console.SetOut(outputWriter);

            try
            {
                var entry = compilation.Assembly!.EntryPoint!;
                var result = entry.Invoke(null, entry.GetParameters().Length == 1 ? new object[] { Array.Empty<string>() } : null);
                if (result is Task task) await task;
            }
            catch (Exception ex)
            {
                return new CompilerRunResponseDto { Error = ex.Message };
            }
            finally
            {
                Console.SetIn(originalIn);
                Console.SetOut(originalOut);
            }

            return new CompilerRunResponseDto { Output = outputWriter.ToString() };
        }

        public async Task<IList<TestResultDto>> RunTestsAsync(string code, IList<TestCaseDto> testCases)
        {
            var compilation = CompileCSharp(code);
            if (!compilation.Success)
                throw new Exception(compilation.Error);

            var entry = compilation.Assembly!.EntryPoint!;
            var results = new List<TestResultDto>();

            foreach (var test in testCases)
            {
                using var inputReader = new StringReader(test.Input ?? "");
                using var outputWriter = new StringWriter(); // пишем как есть, без Trim()
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
                    results.Add(new TestResultDto
                    {
                        Input = test.Input,
                        ExpectedOutput = test.ExpectedOutput,
                        ActualOutput = ex.Message,
                        Passed = false
                    });
                    continue;
                }
                finally
                {
                    Console.SetIn(originalIn);
                    Console.SetOut(originalOut);
                }

                // сырой вывод пользователя (сохраняем без Trim, чтобы фронт мог показать \r\n)
                var actualRaw = outputWriter.ToString();

                // «умное» сравнение: нормализация переводов строк/хвостовых пробелов + числа с допуском
                bool passed = OutputComparer.EqualsSmart(
                    expectedRaw: test.ExpectedOutput ?? string.Empty,
                    actualRaw: actualRaw,
                    eps: 1e-6
                );

                results.Add(new TestResultDto
                {
                    Input = test.Input,
                    ExpectedOutput = test.ExpectedOutput,
                    ActualOutput = actualRaw,
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
    }
}
