using System.Diagnostics;
using System.Text;
using taskforge.Data.Models.DTO;
using taskforge.Services.Interfaces;

namespace taskforge.Services.Compilers
{
    public class CppCompiler : ICompiler
    {
        private readonly ILogger<CppCompiler> _logger;
        private readonly IConfiguration _cfg;

        public CppCompiler(ILogger<CppCompiler> logger, IConfiguration cfg)
        {
            _logger = logger;
            _cfg = cfg;
        }

        private string GppPath => _cfg["Runners:GppPath"] ?? "g++";
        private string ToolBin => Path.GetDirectoryName(GppPath) ?? "";
        private int CompileTimeoutMs => int.TryParse(_cfg["Runners:CompileTimeoutMs"], out var v) ? v : 10000;
        private int RunTimeoutMs => int.TryParse(_cfg["Runners:RunTimeoutMs"], out var v) ? v : 5000;

        public async Task<CompilerRunResponseDto> CompileAndRunAsync(string code, string? input)
        {
            var workDir = Path.Combine(Path.GetTempPath(), "taskforge", "cpp", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workDir);

            var src = Path.Combine(workDir, "main.cpp");
            var exe = Path.Combine(workDir, OperatingSystem.IsWindows() ? "a.exe" : "a.out");

            try
            {
                await File.WriteAllTextAsync(src, code, new UTF8Encoding(false));

                // compile (добавлены статические рантаймы, чтобы не таскать DLL-ки)
                var compileArgs = $"-std=c++17 -O2 -pipe -static-libstdc++ -static-libgcc -o \"{exe}\" \"{src}\"";
                var cr = await RunProcessAsync(GppPath, compileArgs, null, CompileTimeoutMs, workDir, addToPath: ToolBin);
                if (cr.ExitCode != 0)
                {
                    var err = (cr.Stderr ?? "").Trim();
                    if (string.IsNullOrWhiteSpace(err)) err = (cr.Stdout ?? "").Trim();
                    if (string.IsNullOrWhiteSpace(err)) err = $"Compilation failed (exit {cr.ExitCode}).";
                    return new CompilerRunResponseDto { Error = err };
                }

                // run (подкладываем ToolBin в PATH на случай, если всё-таки потребуются DLL)
                var rr = await RunProcessAsync(exe, "", input ?? "", RunTimeoutMs, workDir, addToPath: ToolBin);
                if (rr.TimedOut)
                    return new CompilerRunResponseDto { Error = "Time limit exceeded" };

                if (rr.ExitCode != 0)
                {
                    var err = (rr.Stderr ?? "").Trim();
                    if (string.IsNullOrWhiteSpace(err)) err = $"Runtime error (exit {rr.ExitCode}).";
                    return new CompilerRunResponseDto { Error = err, Output = rr.Stdout };
                }

                return new CompilerRunResponseDto { Output = rr.Stdout };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "CPP CompileAndRun failed");
                return new CompilerRunResponseDto { Error = ex.Message };
            }
            finally
            {
                TryDeleteDir(workDir);
            }
        }

        public async Task<IList<TestResultDto>> RunTestsAsync(string code, IList<TestCaseDto> testCases)
        {
            var workDir = Path.Combine(Path.GetTempPath(), "taskforge", "cpp", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workDir);

            var src = Path.Combine(workDir, "main.cpp");
            var exe = Path.Combine(workDir, OperatingSystem.IsWindows() ? "a.exe" : "a.out");
            var results = new List<TestResultDto>();

            try
            {
                await File.WriteAllTextAsync(src, code, new UTF8Encoding(false));

                // compile once
                var compileArgs = $"-std=c++17 -O2 -pipe -static-libstdc++ -static-libgcc -o \"{exe}\" \"{src}\"";
                var cr = await RunProcessAsync(GppPath, compileArgs, null, CompileTimeoutMs, workDir, addToPath: ToolBin);
                if (cr.ExitCode != 0)
                {
                    var err = string.IsNullOrWhiteSpace(cr.Stderr) ? cr.Stdout : cr.Stderr;
                    throw new Exception((err ?? "").Trim());
                }

                foreach (var t in testCases)
                {
                    try
                    {
                        var rr = await RunProcessAsync(exe, "", t.Input ?? "", RunTimeoutMs, workDir, addToPath: ToolBin);
                        if (rr.TimedOut)
                        {
                            results.Add(new TestResultDto
                            {
                                Input = t.Input,
                                ExpectedOutput = t.ExpectedOutput,
                                ActualOutput = "Time limit exceeded",
                                Passed = false
                            });
                            continue;
                        }

                        var actualRaw = rr.Stdout ?? "";
                        var passed = OutputComparer.EqualsSmart(
                            expectedRaw: t.ExpectedOutput ?? "",
                            actualRaw: actualRaw,
                            eps: 1e-6
                        );

                        if (rr.ExitCode != 0) passed = false;

                        results.Add(new TestResultDto
                        {
                            Input = t.Input,
                            ExpectedOutput = t.ExpectedOutput,
                            ActualOutput = rr.ExitCode == 0 ? actualRaw : (string.IsNullOrWhiteSpace(rr.Stderr) ? actualRaw : rr.Stderr),
                            Passed = passed
                        });
                    }
                    catch (Exception ex)
                    {
                        results.Add(new TestResultDto
                        {
                            Input = t.Input,
                            ExpectedOutput = t.ExpectedOutput,
                            ActualOutput = ex.Message,
                            Passed = false
                        });
                    }
                }

                return results;
            }
            finally
            {
                TryDeleteDir(workDir);
            }
        }

        private async Task<(int ExitCode, string Stdout, string Stderr, bool TimedOut)> RunProcessAsync(
            string fileName, string args, string? stdin, int timeoutMs, string? workingDir, string? addToPath = null)
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = args,
                WorkingDirectory = workingDir ?? "",
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            // важное: добавим tools\ucrt64\bin в PATH дочернего процесса
            if (!string.IsNullOrWhiteSpace(addToPath))
            {
                var currentPath = psi.EnvironmentVariables.ContainsKey("PATH") ? psi.EnvironmentVariables["PATH"] : Environment.GetEnvironmentVariable("PATH");
                psi.EnvironmentVariables["PATH"] = string.IsNullOrWhiteSpace(currentPath)
                    ? addToPath
                    : (addToPath + System.IO.Path.PathSeparator + currentPath);
            }

            using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            proc.Start();

            if (!string.IsNullOrEmpty(stdin))
            {
                await proc.StandardInput.WriteAsync(stdin);
            }
            proc.StandardInput.Close();

            var outTask = proc.StandardOutput.ReadToEndAsync();
            var errTask = proc.StandardError.ReadToEndAsync();
            var waitTask = proc.WaitForExitAsync();

            var finished = await Task.WhenAny(Task.WhenAll(outTask, errTask, waitTask), Task.Delay(timeoutMs));
            if (finished is Task delay && delay.IsCompleted && !waitTask.IsCompleted)
            {
                TryKill(proc);
                return (-1, await outTask, await errTask, true);
            }

            return (proc.ExitCode, await outTask, await errTask, false);
        }

        private void TryKill(Process p)
        {
            try { p.Kill(entireProcessTree: true); } catch { }
        }

        private void TryDeleteDir(string dir)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
        }
    }
}
