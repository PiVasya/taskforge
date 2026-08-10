using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Runner.Models;

namespace Runner.Services;

public sealed class InteractiveConsoleServer : BackgroundService
{
    private const int MaxCodeBytes = 1024 * 1024;
    private const int MaxMessageChars = 128 * 1024;
    private const int MaxStartMessageChars = 1536 * 1024;
    private const int MaxInputBytes = 1024 * 1024;
    private const int MaxOutputBytes = 2 * 1024 * 1024;
    private static readonly TimeSpan CompilationTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaximumRuntime = TimeSpan.FromSeconds(120);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        MaxDepth = 16
    };

    private readonly IRoslynCompilationService _compiler;
    private readonly RunnerJobGate _jobGate;
    private readonly PolicyAttestationVerifier _attestationVerifier;
    private readonly ILogger<InteractiveConsoleServer> _logger;

    public InteractiveConsoleServer(
        IRoslynCompilationService compiler,
        RunnerJobGate jobGate,
        PolicyAttestationVerifier attestationVerifier,
        ILogger<InteractiveConsoleServer> logger)
    {
        _compiler = compiler;
        _jobGate = jobGate;
        _attestationVerifier = attestationVerifier;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var port = ParsePort(Environment.GetEnvironmentVariable("INTERACTIVE_PORT"), 9090);
        var listener = new TcpListener(IPAddress.Any, port);
        listener.Start(16);
        _logger.LogInformation("csharp-runner interactive console listening on :{Port}", port);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                _ = Task.Run(() => HandleClientAsync(client, stoppingToken), CancellationToken.None);
            }
        }
        finally
        {
            listener.Stop();
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken stoppingToken)
    {
        using var ownedClient = client;
        using var stream = ownedClient.GetStream();
        using var reader = new StreamReader(stream, new UTF8Encoding(false), false, 16 * 1024, leaveOpen: true);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false), 16 * 1024, leaveOpen: true) { AutoFlush = true };
        var writeGate = new SemaphoreSlim(1, 1);

        async Task SendAsync(object payload, CancellationToken token = default)
        {
            var json = JsonSerializer.Serialize(payload, JsonOptions);
            await writeGate.WaitAsync(token);
            try
            {
                await writer.WriteLineAsync(json.AsMemory(), token);
            }
            finally
            {
                writeGate.Release();
            }
        }

        string? firstLine;
        using (var startCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken))
        {
            startCts.CancelAfter(TimeSpan.FromSeconds(15));
            try
            {
                firstLine = await reader.ReadLineAsync(startCts.Token);
            }
            catch
            {
                return;
            }
        }
        if (string.IsNullOrWhiteSpace(firstLine) || firstLine.Length > MaxStartMessageChars) return;

        InteractiveMessage? start;
        try
        {
            start = JsonSerializer.Deserialize<InteractiveMessage>(firstLine, JsonOptions);
        }
        catch
        {
            return;
        }
        if (start is null || !string.Equals(start.Type, "start", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(start.Code)) return;
        if (Encoding.UTF8.GetByteCount(start.Code) > MaxCodeBytes)
        {
            await SendAsync(new { type = "error", message = "Код слишком большой." }, stoppingToken);
            return;
        }

        var attestationError = _attestationVerifier.Verify("csharp", "standard", start.Code, start.Attestation);
        if (attestationError is not null)
        {
            _logger.LogWarning("C# runner rejected interactive unattested source: {Reason}", attestationError);
            await SendAsync(new { type = "error", message = "Код не прошёл обязательную проверку безопасности." }, stoppingToken);
            return;
        }

        var entered = false;
        try
        {
            using var gateCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            gateCts.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                await _jobGate.EnterAsync(gateCts.Token);
                entered = true;
            }
            catch
            {
                await SendAsync(new { type = "error", message = "Раннер занят. Повторите запуск через несколько секунд." }, stoppingToken);
                return;
            }

            await SendAsync(new { type = "status", phase = "compiling", message = "Компиляция..." }, stoppingToken);
            RoslynCompilationResult compilation;
            using (var compileCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken))
            {
                compileCts.CancelAfter(CompilationTimeout);
                try
                {
                    compilation = _compiler.Compile(start.Code, compileCts.Token);
                }
                catch (OperationCanceledException)
                {
                    await SendAsync(new { type = "output", stream = "stderr", data = "Компиляция превысила лимит времени.\r\n" }, stoppingToken);
                    await SendAsync(new { type = "exit", exitCode = 124, reason = "compile_time_limit", durationMs = 0 }, stoppingToken);
                    return;
                }
            }

            if (!compilation.Ok || compilation.Pe is null)
            {
                var policyFailure = compilation.FailureKind == CompilationFailureKind.PolicyError;
                var message = policyFailure ? "Решение отклонено системой безопасности." : compilation.Error;
                await SendAsync(new { type = "output", stream = "stderr", data = NormalizeTerminalText(message) }, stoppingToken);
                await SendAsync(new
                {
                    type = "exit",
                    exitCode = policyFailure ? 126 : 1,
                    reason = policyFailure ? "policy_error" : "compile_error",
                    durationMs = 0
                }, stoppingToken);
                return;
            }

            var workDirectory = Path.Combine(Path.GetTempPath(), "taskforge-csharp-interactive-" + Guid.NewGuid().ToString("N"));
            if (OperatingSystem.IsWindows())
            {
                Directory.CreateDirectory(workDirectory);
            }
            else
            {
                Directory.CreateDirectory(workDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            try
            {
                var assemblyPath = Path.Combine(workDirectory, "UserSubmission.dll");
                var pdbPath = Path.Combine(workDirectory, "UserSubmission.pdb");
                await File.WriteAllBytesAsync(assemblyPath, compilation.Pe, stoppingToken);
                if (compilation.Pdb is { Length: > 0 }) await File.WriteAllBytesAsync(pdbPath, compilation.Pdb, stoppingToken);

                await RunProcessAsync(start, reader, SendAsync, workDirectory, assemblyPath, File.Exists(pdbPath) ? pdbPath : null, stoppingToken);
            }
            finally
            {
                TryDeleteDirectory(workDirectory);
            }
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "C# interactive session failed");
            try { await SendAsync(new { type = "error", message = "Интерактивная сессия завершилась с ошибкой." }, stoppingToken); } catch { }
        }
        finally
        {
            if (entered) _jobGate.Exit();
            writeGate.Dispose();
        }
    }

    private static async Task RunProcessAsync(
        InteractiveMessage start,
        StreamReader reader,
        Func<object, CancellationToken, Task> sendAsync,
        string workDirectory,
        string assemblyPath,
        string? pdbPath,
        CancellationToken stoppingToken)
    {
        var entryAssembly = Assembly.GetEntryAssembly()?.Location;
        if (string.IsNullOrWhiteSpace(entryAssembly))
        {
            await sendAsync(new { type = "error", message = "Среда C# временно недоступна." }, stoppingToken);
            return;
        }
        var preload = Environment.GetEnvironmentVariable("TASKFORGE_SANDBOX_PRELOAD") ?? "/app/libtaskforge_sandbox.so";
        if (!File.Exists(preload))
        {
            await sendAsync(new { type = "error", message = "Защищённая среда C# временно недоступна." }, stoppingToken);
            return;
        }

        var columns = Math.Clamp(start.Columns <= 0 ? 110 : start.Columns, 40, 240);
        var rows = Math.Clamp(start.Rows <= 0 ? 30 : start.Rows, 10, 80);
        var requestedMs = start.TimeLimitMs <= 0 ? (int)MaximumRuntime.TotalMilliseconds : start.TimeLimitMs;
        var runtimeMs = Math.Clamp(requestedMs, 1000, (int)MaximumRuntime.TotalMilliseconds);
        var memoryMb = Math.Clamp(start.MemoryLimitMb <= 0 ? 256 : start.MemoryLimitMb, 64, 512);
        var cpuSeconds = Math.Max(2, (runtimeMs + 999) / 1000 + 2);
        var heapBytes = Math.Clamp((long)memoryMb * 1024 * 1024 * 3 / 4, 32L * 1024 * 1024, 384L * 1024 * 1024);

        var exports = new Dictionary<string, string>
        {
            ["HOME"] = workDirectory,
            ["TMPDIR"] = workDirectory,
            ["TMP"] = workDirectory,
            ["TEMP"] = workDirectory,
            ["TERM"] = "xterm-256color",
            ["COLORTERM"] = "truecolor",
            ["TASKFORGE_SUBMISSION"] = "1",
            ["TASKFORGE_SANDBOX_PROFILE"] = "managed",
            ["TASKFORGE_LIMIT_CPU_SECONDS"] = cpuSeconds.ToString(CultureInfo.InvariantCulture),
            ["TASKFORGE_LIMIT_FSIZE_MB"] = "16",
            ["TASKFORGE_LIMIT_NOFILE"] = "128",
            ["LD_PRELOAD"] = preload,
            ["DOTNET_EnableDiagnostics"] = "0",
            ["DOTNET_EnableDiagnostics_IPC"] = "0",
            ["DOTNET_EnableDiagnostics_Debugger"] = "0",
            ["DOTNET_EnableDiagnostics_Profiler"] = "0",
            ["COMPlus_EnableDiagnostics"] = "0",
            ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
            ["DOTNET_NOLOGO"] = "1",
            ["DOTNET_GCHeapHardLimit"] = heapBytes.ToString("x", CultureInfo.InvariantCulture),
            ["COMPlus_GCHeapHardLimit"] = heapBytes.ToString("x", CultureInfo.InvariantCulture)
        };

        var parts = new List<string> { $"stty cols {columns} rows {rows}" };
        parts.AddRange(exports.Select(pair => $"export {pair.Key}={ShellQuote(pair.Value)}"));
        var command = $"dotnet {ShellQuote(entryAssembly)} --interactive-exec {ShellQuote(assemblyPath)}";
        if (!string.IsNullOrWhiteSpace(pdbPath)) command += " " + ShellQuote(pdbPath);
        parts.Add("exec " + command);

        var startInfo = new ProcessStartInfo
        {
            FileName = "script",
            WorkingDirectory = workDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-qefc");
        startInfo.ArgumentList.Add(string.Join("; ", parts));
        startInfo.ArgumentList.Add("/dev/null");
        startInfo.Environment.Clear();
        CopyEnvironment(startInfo, "PATH");
        CopyEnvironment(startInfo, "DOTNET_ROOT");
        CopyEnvironment(startInfo, "LANG");
        CopyEnvironment(startInfo, "LC_ALL");
        CopyEnvironment(startInfo, "TZ");

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var startedAt = Stopwatch.StartNew();
        if (!process.Start())
        {
            await sendAsync(new { type = "error", message = "Не удалось запустить программу." }, stoppingToken);
            return;
        }
        await sendAsync(new { type = "status", phase = "running", message = "Программа запущена", pid = process.Id }, stoppingToken);

        var outputBytes = 0L;
        var outputLimit = false;
        var outputLock = new object();
        void StopProcess()
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
        }

        async Task CopyOutputAsync(Stream source, string streamName, CancellationToken token)
        {
            var buffer = new byte[4096];
            var chars = new char[Encoding.UTF8.GetMaxCharCount(buffer.Length)];
            var decoder = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false).GetDecoder();
            try
            {
                while (true)
                {
                    var read = await source.ReadAsync(buffer, token);
                    if (read <= 0) break;
                    var sendCount = read;
                    var announceOutputLimit = false;
                    var limitReached = false;
                    lock (outputLock)
                    {
                        var left = MaxOutputBytes - outputBytes;
                        if (left <= 0) sendCount = 0;
                        else if (sendCount > left) sendCount = (int)left;
                        outputBytes += sendCount;
                        if (outputBytes >= MaxOutputBytes && !outputLimit)
                        {
                            outputLimit = true;
                            announceOutputLimit = true;
                        }
                        limitReached = outputLimit;
                    }
                    if (sendCount > 0)
                    {
                        var charCount = decoder.GetChars(buffer, 0, sendCount, chars, 0, flush: false);
                        if (charCount > 0)
                        {
                            await sendAsync(new { type = "output", stream = streamName, data = new string(chars, 0, charCount) }, token);
                        }
                    }
                    if (limitReached)
                    {
                        if (announceOutputLimit)
                        {
                            await sendAsync(new { type = "output", stream = "system", data = "\r\n[TaskForge] Вывод остановлен: превышено ограничение 2 МБ.\r\n" }, token);
                        }
                        StopProcess();
                        break;
                    }
                }
            }
            finally
            {
                try
                {
                    var charCount = decoder.GetChars(Array.Empty<byte>(), 0, 0, chars, 0, flush: true);
                    if (charCount > 0)
                    {
                        await sendAsync(new { type = "output", stream = streamName, data = new string(chars, 0, charCount) }, CancellationToken.None);
                    }
                }
                catch
                {
                }
            }
        }

        using var runtimeCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        runtimeCts.CancelAfter(TimeSpan.FromMilliseconds(runtimeMs + 3000));
        var stdoutTask = CopyOutputAsync(process.StandardOutput.BaseStream, "stdout", runtimeCts.Token);
        var stderrTask = CopyOutputAsync(process.StandardError.BaseStream, "stderr", runtimeCts.Token);
        var inputTask = Task.Run(async () =>
        {
            var inputBytes = 0;
            while (!runtimeCts.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(runtimeCts.Token);
                if (line is null || line.Length > MaxMessageChars)
                {
                    StopProcess();
                    break;
                }
                InteractiveMessage? message;
                try { message = JsonSerializer.Deserialize<InteractiveMessage>(line, JsonOptions); }
                catch { continue; }
                if (message is null) continue;
                switch (message.Type)
                {
                    case "input":
                        inputBytes += Encoding.UTF8.GetByteCount(message.Data ?? string.Empty);
                        if (inputBytes > MaxInputBytes)
                        {
                            await sendAsync(new { type = "output", stream = "system", data = "\r\n[TaskForge] Ввод остановлен: превышено ограничение 1 МБ.\r\n" }, runtimeCts.Token);
                            StopProcess();
                            return;
                        }
                        await process.StandardInput.WriteAsync((message.Data ?? string.Empty).AsMemory(), runtimeCts.Token);
                        await process.StandardInput.FlushAsync(runtimeCts.Token);
                        break;
                    case "interrupt":
                        await process.StandardInput.WriteAsync("\u0003".AsMemory(), runtimeCts.Token);
                        await process.StandardInput.FlushAsync(runtimeCts.Token);
                        break;
                    case "stop":
                        StopProcess();
                        return;
                }
            }
        }, CancellationToken.None);

        try
        {
            await process.WaitForExitAsync(runtimeCts.Token);
        }
        catch (OperationCanceledException)
        {
            StopProcess();
        }
        try { await Task.WhenAll(stdoutTask, stderrTask); } catch { }
        runtimeCts.Cancel();
        try { await inputTask; } catch { }

        var exitCode = 0;
        try { exitCode = process.HasExited ? process.ExitCode : 1; } catch { exitCode = 1; }
        var reason = exitCode == 0 ? "completed" : "runtime_error";
        if (outputLimit)
        {
            reason = "output_limit";
            exitCode = 125;
        }
        else if (startedAt.ElapsedMilliseconds >= runtimeMs || !process.HasExited)
        {
            reason = "time_limit";
            exitCode = 124;
            await sendAsync(new { type = "output", stream = "system", data = "\r\n[TaskForge] Процесс завершён по тайм-ауту.\r\n" }, stoppingToken);
        }
        await sendAsync(new { type = "exit", exitCode, reason, durationMs = startedAt.ElapsedMilliseconds }, stoppingToken);
    }

    private static void CopyEnvironment(ProcessStartInfo startInfo, string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (!string.IsNullOrWhiteSpace(value)) startInfo.Environment[name] = value;
    }

    private static string NormalizeTerminalText(string value)
    {
        value = string.IsNullOrWhiteSpace(value) ? "Компиляция завершилась с ошибкой." : value;
        return value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal);
    }

    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    private static int ParsePort(string? raw, int fallback) => int.TryParse(raw, out var parsed) && parsed is > 0 and <= 65535 ? parsed : fallback;

    private static void TryDeleteDirectory(string path)
    {
        try { Directory.Delete(path, recursive: true); } catch { }
    }

    private sealed class InteractiveMessage
    {
        public string Type { get; init; } = string.Empty;
        public string Code { get; init; } = string.Empty;
        public string? Data { get; init; }
        public int Columns { get; init; }
        public int Rows { get; init; }
        public int TimeLimitMs { get; init; }
        public int MemoryLimitMb { get; init; }
        public PolicyAttestation? Attestation { get; init; }
    }
}
