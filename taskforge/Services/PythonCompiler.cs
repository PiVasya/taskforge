using System.Diagnostics;
using System.Text;
using taskforge.Data.Models.DTO;
using taskforge.Services.Interfaces;

namespace taskforge.Services.Compilers
{
    public class PythonCompiler : ICompiler
    {
        private readonly ILogger<PythonCompiler> _logger;
        private readonly IConfiguration _cfg;

        public PythonCompiler(ILogger<PythonCompiler> logger, IConfiguration cfg)
        {
            _logger = logger;
            _cfg = cfg;
        }

        private string PythonPath
        {
            get
            {
                var fromCfg = _cfg["Runners:PythonPath"];
                if (!string.IsNullOrWhiteSpace(fromCfg)) return fromCfg!;
                return OperatingSystem.IsWindows() ? "python" : "python3";
            }
        }

        private int RunTimeoutMs => int.TryParse(_cfg["Runners:RunTimeoutMs"], out var v) ? v : 5000;

        public async Task<CompilerRunResponseDto> CompileAndRunAsync(string code, string? input)
        {
            var workDir = Path.Combine(Path.GetTempPath(), "taskforge", "py", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workDir);
            var py = Path.Combine(workDir, "main.py");

            try
            {
                await File.WriteAllTextAsync(py, code, new UTF8Encoding(false));

                var rr = await RunProcessAsync(PythonPath, $"\"{py}\"", input ?? "", RunTimeoutMs, workDir);
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
                _logger.LogError(ex, "Python run failed");
                return new CompilerRunResponseDto { Error = ex.Message };
            }
            finally
            {
                TryDeleteDir(workDir);
            }
        }

        public async Task<IList<TestResultDto>> RunTestsAsync(string code, IList<TestCaseDto> testCases)
        {
            var workDir = Path.Combine(Path.GetTempPath(), "taskforge", "py", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workDir);
            var py = Path.Combine(workDir, "main.py");
            var results = new List<TestResultDto>();

            try
            {
                await File.WriteAllTextAsync(py, code, new UTF8Encoding(false));

                foreach (var t in testCases)
                {
                    try
                    {
                        var rr = await RunProcessAsync(PythonPath, $"\"{py}\"", t.Input ?? "", RunTimeoutMs, workDir);
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
            string fileName, string args, string? stdin, int timeoutMs, string? workingDir)
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

            using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            proc.Start();

            if (!string.IsNullOrEmpty(stdin))
                await proc.StandardInput.WriteAsync(stdin);
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
