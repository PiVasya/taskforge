// taskforge/Controllers/TestsController.cs
using Microsoft.AspNetCore.Mvc;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace TaskForge.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class TestsController : ControllerBase
    {
        [HttpPost("run-tests")]
        public async Task<IActionResult> RunTests([FromBody] TestRunRequestDto request)
        {
            if (request.TestCases == null || request.TestCases.Count == 0)
            {
                return BadRequest(new { error = "No test cases provided." });
            }

            try
            {
                List<TestCaseResultDto> results = request.Language.ToLowerInvariant() switch
                {
                    "csharp" or "c#" => await RunCSharpTests(request.Code, request.TestCases),
                    "cpp" or "c++" => await RunCppTests(request.Code, request.TestCases),
                    _ => throw new Exception("Unsupported language")
                };

                return Ok(new { results });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }

        // C# tests
        private async Task<List<TestCaseResultDto>> RunCSharpTests(string code, List<TestCaseDto> testCases)
        {
            var syntaxTree = CSharpSyntaxTree.ParseText(code);
            var references = new List<MetadataReference>
            {
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(Console).Assembly.Location),
                MetadataReference.CreateFromFile(Assembly.Load("System.Runtime").Location)
            };
            var compilation = CSharpCompilation.Create(
                "UserProgram",
                new[] { syntaxTree },
                references,
                new CSharpCompilationOptions(OutputKind.ConsoleApplication));

            await using var ms = new MemoryStream();
            var emitResult = compilation.Emit(ms);
            if (!emitResult.Success)
            {
                var errors = new StringBuilder();
                foreach (var diagnostic in emitResult.Diagnostics)
                    errors.AppendLine(diagnostic.ToString());
                throw new Exception(errors.ToString());
            }

            ms.Seek(0, SeekOrigin.Begin);
            var assemblyBytes = ms.ToArray();
            var assembly = Assembly.Load(assemblyBytes);
            var entry = assembly.EntryPoint!;

            var results = new List<TestCaseResultDto>();

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
                    var result = entry.Invoke(null, entry.GetParameters().Length == 1 ? new object[] { Array.Empty<string>() } : null);
                    if (result is Task t) await t;
                }
                finally
                {
                    Console.SetIn(originalIn);
                    Console.SetOut(originalOut);
                }
                var actual = outputWriter.ToString().Trim();
                results.Add(new TestCaseResultDto
                {
                    Input = test.Input,
                    ExpectedOutput = test.ExpectedOutput,
                    ActualOutput = actual,
                    Passed = actual == (test.ExpectedOutput?.Trim() ?? "")
                });
            }

            return results;
        }

        // C++ tests (компиляция аналогична выше)
        private async Task<List<TestCaseResultDto>> RunCppTests(string code, List<TestCaseDto> testCases)
        {
            var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(tempDir);
            var codeFile = Path.Combine(tempDir, "program.cpp");
            var exeFile = Path.Combine(tempDir, "program.exe");
            await System.IO.File.WriteAllTextAsync(codeFile, code);

            // компиляция
            var compile = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "g++",
                    Arguments = $"-std=c++17 \"{codeFile}\" -o \"{exeFile}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            compile.Start();
            var compileError = await compile.StandardError.ReadToEndAsync();
            compile.WaitForExit();
            if (compile.ExitCode != 0)
            {
                throw new Exception(compileError);
            }

            var results = new List<TestCaseResultDto>();
            foreach (var test in testCases)
            {
                var run = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = exeFile,
                        RedirectStandardInput = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };
                run.Start();
                if (!string.IsNullOrEmpty(test.Input))
                {
                    await run.StandardInput.WriteAsync(test.Input);
                    run.StandardInput.Close();
                }
                var output = await run.StandardOutput.ReadToEndAsync();
                var error = await run.StandardError.ReadToEndAsync();
                run.WaitForExit();
                if (!string.IsNullOrWhiteSpace(error))
                {
                    results.Add(new TestCaseResultDto
                    {
                        Input = test.Input,
                        ExpectedOutput = test.ExpectedOutput,
                        ActualOutput = error.Trim(),
                        Passed = false
                    });
                }
                else
                {
                    var actual = output.Trim();
                    results.Add(new TestCaseResultDto
                    {
                        Input = test.Input,
                        ExpectedOutput = test.ExpectedOutput,
                        ActualOutput = actual,
                        Passed = actual == (test.ExpectedOutput?.Trim() ?? "")
                    });
                }
            }
            return results;
        }
    }

    // DTO classes
    public class TestRunRequestDto
    {
        public string Language { get; set; }
        public string Code { get; set; }
        public List<TestCaseDto> TestCases { get; set; }
    }
    public class TestCaseDto
    {
        public string Input { get; set; }
        public string ExpectedOutput { get; set; }
    }
    public class TestCaseResultDto
    {
        public string Input { get; set; }
        public string ExpectedOutput { get; set; }
        public string ActualOutput { get; set; }
        public bool Passed { get; set; }
    }
}
