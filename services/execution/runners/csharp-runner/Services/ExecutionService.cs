using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace Runner.Services;

public sealed class ExecutionService : IExecutionService
{
    private const int MaxProtocolChars = RunnerLimits.MaxOutputChars + 64 * 1024;
    private const int MaxDiagnosticChars = 64 * 1024;
    private const int MaxAssemblyBytes = 16 * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        MaxDepth = 16
    };

    private sealed class ExecRequest
    {
        public string AssemblyBase64 { get; set; } = "";
        public string? PdbBase64 { get; set; }
        public string? Input { get; set; }
    }

    private sealed class ExecResponse
    {
        public bool Ok { get; set; }
        public string Stdout { get; set; } = "";
        public string Error { get; set; } = "";
    }

    public async Task<(bool Ok, string Stdout, string Error, string Status)> RunAsync(
        byte[] pe,
        byte[] pdb,
        string input,
        int timeLimitMs,
        int memoryLimitMb,
        CancellationToken cancellationToken)
    {
        if (pe.Length == 0 || pe.Length > MaxAssemblyBytes || pdb.Length > MaxAssemblyBytes)
        {
            return (false, "", "Решение отклонено системой безопасности.", "policy_error");
        }

        var normalizedInput = string.IsNullOrEmpty(input) ? "\n" : input;
        var entryDll = Assembly.GetEntryAssembly()?.Location;
        if (string.IsNullOrWhiteSpace(entryDll))
        {
            return (false, "", "Runner entry assembly location not found.", "policy_error");
        }

        var preloadPath = Environment.GetEnvironmentVariable("TASKFORGE_SANDBOX_PRELOAD");
        if (string.IsNullOrWhiteSpace(preloadPath))
        {
            preloadPath = "/app/libtaskforge_sandbox.so";
        }
        if (!File.Exists(preloadPath) || (File.GetAttributes(preloadPath) & FileAttributes.Directory) != 0)
        {
            return (false, "", "Решение отклонено системой безопасности.", "policy_error");
        }

        var workDirectory = CreateWorkDirectory();
        try
        {
            var request = new ExecRequest
            {
                AssemblyBase64 = Convert.ToBase64String(pe),
                PdbBase64 = pdb.Length > 0 ? Convert.ToBase64String(pdb) : null,
                Input = normalizedInput
            };
            var json = JsonSerializer.Serialize(request, JsonOpts);

            var processStartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = workDirectory
            };
            processStartInfo.ArgumentList.Add(entryDll);
            processStartInfo.ArgumentList.Add("--exec");

            PopulateSandboxEnvironment(processStartInfo, preloadPath, workDirectory, timeLimitMs, memoryLimitMb);

            using var process = new Process { StartInfo = processStartInfo, EnableRaisingEvents = false };
            try
            {
                if (!process.Start())
                {
                    return (false, "", "Execution failed.", "runtime_error");
                }

                var stdoutTask = ReadLimitedAsync(process.StandardOutput, MaxProtocolChars, cancellationToken);
                var stderrTask = ReadLimitedAsync(process.StandardError, MaxDiagnosticChars, cancellationToken);

                await process.StandardInput.WriteAsync(json.AsMemory(), cancellationToken);
                await process.StandardInput.FlushAsync(cancellationToken);
                process.StandardInput.Close();

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(timeLimitMs + 2_000));

                try
                {
                    await process.WaitForExitAsync(timeoutCts.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    TryKill(process);
                    await WaitAfterKillAsync(process);
                    _ = await stdoutTask;
                    _ = await stderrTask;
                    return (false, "", "Time limit exceeded.", "time_limit");
                }

                var childStdout = await stdoutTask;
                var childStderr = await stderrTask;

                if (process.ExitCode == 126)
                {
                    return (false, "", "Решение отклонено системой безопасности.", "policy_error");
                }
                if (string.IsNullOrWhiteSpace(childStdout))
                {
                    var error = string.IsNullOrWhiteSpace(childStderr) ? "Execution failed." : childStderr;
                    if (IsRunnerInfrastructureDiagnostic(error))
                    {
                        return (false, "", "Runner process resources are temporarily unavailable.", "judge_unavailable");
                    }
                    return (false, "", error, "runtime_error");
                }

                ExecResponse? response;
                try
                {
                    response = JsonSerializer.Deserialize<ExecResponse>(childStdout, JsonOpts);
                }
                catch (JsonException)
                {
                    var error = "Bad exec response.";
                    if (!string.IsNullOrWhiteSpace(childStderr))
                    {
                        error += "\n" + childStderr;
                    }
                    return (false, "", error, "runtime_error");
                }

                if (response is null)
                {
                    return (false, "", "Empty exec response.", "runtime_error");
                }

                return response.Ok
                    ? (true, Limit(response.Stdout, RunnerLimits.MaxOutputChars), "", "ok")
                    : (false, Limit(response.Stdout, RunnerLimits.MaxOutputChars), Limit(response.Error, MaxDiagnosticChars), "runtime_error");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                TryKill(process);
                await WaitAfterKillAsync(process);
                throw;
            }
            catch (Exception ex) when (IsRunnerInfrastructureFailure(ex))
            {
                TryKill(process);
                await WaitAfterKillAsync(process);
                return (false, "", "Runner process resources are temporarily unavailable.", "judge_unavailable");
            }
            catch (Exception ex)
            {
                TryKill(process);
                await WaitAfterKillAsync(process);
                return (false, "", Limit(ex.Message, MaxDiagnosticChars), "runtime_error");
            }
        }
        finally
        {
            TryDeleteWorkDirectory(workDirectory);
        }
    }


    private static bool IsRunnerInfrastructureFailure(Exception exception)
    {
        // Process.Start on Linux reports parent/container resource exhaustion as a
        // Win32Exception. These are judge failures, not failures of the student's code.
        if (exception is Win32Exception win32 && win32.NativeErrorCode is 11 or 12 or 23 or 24)
        {
            return true;
        }

        if (IsRunnerInfrastructureDiagnostic(exception.Message))
        {
            return true;
        }

        return exception.InnerException is not null && IsRunnerInfrastructureFailure(exception.InnerException);
    }

    private static bool IsRunnerInfrastructureDiagnostic(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.Contains("Too many open files", StringComparison.OrdinalIgnoreCase)
            || value.Contains("error=24", StringComparison.OrdinalIgnoreCase)
            || value.Contains("EMFILE", StringComparison.OrdinalIgnoreCase)
            || value.Contains("ENFILE", StringComparison.OrdinalIgnoreCase);
    }

    private static void PopulateSandboxEnvironment(ProcessStartInfo startInfo, string preloadPath, string workDirectory, int timeLimitMs, int memoryLimitMb)
    {
        var inheritedPath = Environment.GetEnvironmentVariable("PATH");
        var dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        var globalizationInvariant = Environment.GetEnvironmentVariable("DOTNET_SYSTEM_GLOBALIZATION_INVARIANT");
        var language = Environment.GetEnvironmentVariable("LANG");
        var locale = Environment.GetEnvironmentVariable("LC_ALL");
        var timezone = Environment.GetEnvironmentVariable("TZ");

        startInfo.Environment.Clear();
        CopyIfPresent(startInfo, "PATH", inheritedPath);
        CopyIfPresent(startInfo, "DOTNET_ROOT", dotnetRoot);
        CopyIfPresent(startInfo, "DOTNET_SYSTEM_GLOBALIZATION_INVARIANT", globalizationInvariant);
        CopyIfPresent(startInfo, "LANG", language);
        CopyIfPresent(startInfo, "LC_ALL", locale);
        CopyIfPresent(startInfo, "TZ", timezone);

        var cpuSeconds = Math.Max(2, (timeLimitMs + 999) / 1_000 + 2);
        var heapBytes = Math.Clamp((long)memoryLimitMb * 1024 * 1024 * 3 / 4, 32L * 1024 * 1024, 768L * 1024 * 1024);

        startInfo.Environment["HOME"] = workDirectory;
        startInfo.Environment["TMPDIR"] = workDirectory;
        startInfo.Environment["TMP"] = workDirectory;
        startInfo.Environment["TEMP"] = workDirectory;
        startInfo.Environment["TASKFORGE_SUBMISSION"] = "1";
        startInfo.Environment["TASKFORGE_SANDBOX_PROFILE"] = "managed";
        startInfo.Environment["TASKFORGE_LIMIT_CPU_SECONDS"] = cpuSeconds.ToString(CultureInfo.InvariantCulture);
        startInfo.Environment["TASKFORGE_LIMIT_FSIZE_MB"] = "16";
        startInfo.Environment["TASKFORGE_LIMIT_NOFILE"] = "128";
        startInfo.Environment["LD_PRELOAD"] = preloadPath;
        startInfo.Environment["DOTNET_EnableDiagnostics"] = "0";
        startInfo.Environment["DOTNET_EnableDiagnostics_IPC"] = "0";
        startInfo.Environment["DOTNET_EnableDiagnostics_Debugger"] = "0";
        startInfo.Environment["DOTNET_EnableDiagnostics_Profiler"] = "0";
        startInfo.Environment["COMPlus_EnableDiagnostics"] = "0";
        startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        startInfo.Environment["DOTNET_NOLOGO"] = "1";
        startInfo.Environment["DOTNET_GCHeapHardLimit"] = heapBytes.ToString("x", CultureInfo.InvariantCulture);
        startInfo.Environment["COMPlus_GCHeapHardLimit"] = heapBytes.ToString("x", CultureInfo.InvariantCulture);
    }

    private static string CreateWorkDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "taskforge-csharp-" + Guid.NewGuid().ToString("N"));
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
        }
        else
        {
            Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        return path;
    }

    private static void TryDeleteWorkDirectory(string path)
    {
        try
        {
            DeleteTreeWithoutFollowingLinks(path);
        }
        catch
        {
        }
    }

    private static void DeleteTreeWithoutFollowingLinks(string path)
    {
        var directory = new DirectoryInfo(path);
        directory.Refresh();
        if (directory.LinkTarget is not null || (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0))
        {
            directory.Delete();
            return;
        }
        if (!directory.Exists)
        {
            return;
        }
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        directory.Refresh();

        foreach (var entry in directory.EnumerateFileSystemInfos())
        {
            entry.Refresh();
            if (entry.LinkTarget is not null || (entry.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                entry.Delete();
                continue;
            }

            if ((entry.Attributes & FileAttributes.Directory) != 0)
            {
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(entry.FullName, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                }
                DeleteTreeWithoutFollowingLinks(entry.FullName);
            }
            else
            {
                entry.Delete();
            }
        }
        directory.Delete();
    }

    private static void CopyIfPresent(ProcessStartInfo startInfo, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            startInfo.Environment[name] = value;
        }
    }

    private static async Task<string> ReadLimitedAsync(StreamReader reader, int maxChars, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder(Math.Min(maxChars, 16 * 1024));
        var buffer = new char[8 * 1024];
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0)
            {
                break;
            }
            var remaining = maxChars - builder.Length;
            if (remaining > 0)
            {
                builder.Append(buffer, 0, Math.Min(read, remaining));
            }
        }
        return builder.ToString();
    }

    private static string Limit(string? value, int maxChars)
    {
        var text = value ?? string.Empty;
        return text.Length <= maxChars ? text : text[..maxChars];
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
    }

    private static async Task WaitAfterKillAsync(Process process)
    {
        try
        {
            using var waitCts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            await process.WaitForExitAsync(waitCts.Token);
        }
        catch
        {
        }
    }
}
