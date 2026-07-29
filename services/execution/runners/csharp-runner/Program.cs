using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Serialization;
using Runner.Services;

const string ExecArg = "--exec";
const string InteractiveExecArg = "--interactive-exec";

if (args.Length > 0 && string.Equals(args[0], InteractiveExecArg, StringComparison.OrdinalIgnoreCase))
{
    InteractiveExecMode.Run(args);
    return;
}

if (args.Any(argument => string.Equals(argument, ExecArg, StringComparison.OrdinalIgnoreCase)))
{
    ExecMode.Run();
    return;
}

NativeHardening.ProtectServiceProcess();

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = RunnerLimits.MaxRequestBytes;
    options.Limits.MaxRequestHeaderCount = 64;
    options.Limits.MaxRequestHeadersTotalSize = 32 * 1024;
    options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
    options.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(30);
});

builder.Services.AddTaskForgeDebugDiagnostics("csharp-runner");
builder.Services.AddControllers().AddJsonOptions(options =>
{
    options.JsonSerializerOptions.MaxDepth = 16;
    options.JsonSerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
});
builder.Services.AddSingleton<IRoslynCompilationService, RoslynCompilationService>();
builder.Services.AddSingleton<IExecutionService, ExecutionService>();
builder.Services.AddSingleton<RunnerJobGate>();
builder.Services.AddSingleton<PolicyAttestationVerifier>();
builder.Services.AddHostedService<InteractiveConsoleServer>();

var app = builder.Build();
_ = app.Services.GetRequiredService<PolicyAttestationVerifier>();
app.UseTaskForgeDebugRequestLogging("csharp-runner");
app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "csharp-runner" }));
app.MapGet("/ready", (PolicyAttestationVerifier _) => File.Exists(Environment.GetEnvironmentVariable("TASKFORGE_SANDBOX_PRELOAD") ?? "/app/libtaskforge_sandbox.so")
    ? Results.Ok(new { status = "ready" })
    : Results.StatusCode(StatusCodes.Status503ServiceUnavailable));
app.MapControllers();
app.Run();


static class InteractiveExecMode
{
    public static void Run(string[] arguments)
    {
        try
        {
            if (arguments.Length < 2)
            {
                Console.Error.WriteLine("Interactive assembly path is missing.");
                Environment.Exit(2);
            }
            var assemblyPath = Path.GetFullPath(arguments[1]);
            var pdbPath = arguments.Length > 2 ? Path.GetFullPath(arguments[2]) : null;
            var loadContext = new AssemblyLoadContext("interactive-user-submission", isCollectible: false);
            Assembly assembly;
            using (var pe = File.OpenRead(assemblyPath))
            {
                if (!string.IsNullOrWhiteSpace(pdbPath) && File.Exists(pdbPath))
                {
                    using var pdb = File.OpenRead(pdbPath);
                    assembly = loadContext.LoadFromStream(pe, pdb);
                }
                else
                {
                    assembly = loadContext.LoadFromStream(pe);
                }
            }
            NativeLibrary.SetDllImportResolver(assembly, static (libraryName, _, _) =>
                throw new DllNotFoundException($"Native library '{libraryName}' is not available in the runner."));
            var entryPoint = assembly.EntryPoint ?? throw new InvalidOperationException("Entry point not found.");
            object? result = entryPoint.GetParameters().Length == 0
                ? entryPoint.Invoke(null, null)
                : entryPoint.Invoke(null, [Array.Empty<string>()]);
            if (result is Task task) task.GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.InnerException?.Message ?? ex.Message);
            Environment.Exit(1);
        }
    }
}

static class ExecMode
{
    private const int MaxAssemblyBytes = 16 * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        MaxDepth = 16,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
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

    public static void Run()
    {
        try
        {
            var json = Console.In.ReadToEnd();
            var request = JsonSerializer.Deserialize<ExecRequest>(json, JsonOpts);
            if (request is null || string.IsNullOrWhiteSpace(request.AssemblyBase64))
            {
                Write(new ExecResponse { Ok = false, Error = "Bad exec request." });
                return;
            }

            var pe = Convert.FromBase64String(request.AssemblyBase64);
            var pdb = string.IsNullOrWhiteSpace(request.PdbBase64)
                ? []
                : Convert.FromBase64String(request.PdbBase64);
            if (pe.Length == 0 || pe.Length > MaxAssemblyBytes || pdb.Length > MaxAssemblyBytes)
            {
                Write(new ExecResponse { Ok = false, Error = "Bad exec request." });
                return;
            }

            var originalIn = Console.In;
            var originalOut = Console.Out;
            var originalError = Console.Error;
            using var inputReader = new StringReader(string.IsNullOrEmpty(request.Input) ? "\n" : request.Input);
            using var outputWriter = new CappedTextWriter(RunnerLimits.MaxOutputChars);
            using var errorWriter = new CappedTextWriter(64 * 1024);

            Console.SetIn(inputReader);
            Console.SetOut(outputWriter);
            Console.SetError(errorWriter);

            var response = new ExecResponse();
            AssemblyLoadContext? loadContext = null;
            try
            {
                loadContext = new AssemblyLoadContext("user-submission", isCollectible: true);
                using var peStream = new MemoryStream(pe, writable: false);
                using var pdbStream = pdb.Length > 0 ? new MemoryStream(pdb, writable: false) : null;
                var assembly = pdbStream is null
                    ? loadContext.LoadFromStream(peStream)
                    : loadContext.LoadFromStream(peStream, pdbStream);

                NativeLibrary.SetDllImportResolver(assembly, static (libraryName, _, _) =>
                    throw new DllNotFoundException($"Native library '{libraryName}' is not available in the runner."));

                var entryPoint = assembly.EntryPoint;
                if (entryPoint is null)
                {
                    response.Ok = false;
                    response.Error = "Entry point not found.";
                }
                else
                {
                    object? result = entryPoint.GetParameters().Length == 0
                        ? entryPoint.Invoke(null, null)
                        : entryPoint.Invoke(null, [Array.Empty<string>()]);
                    if (result is Task task)
                    {
                        task.GetAwaiter().GetResult();
                    }
                    response.Ok = true;
                }
            }
            catch (Exception ex)
            {
                response.Ok = false;
                response.Error = ex.InnerException?.Message ?? ex.Message;
            }
            finally
            {
                response.Stdout = outputWriter.ToString();
                Console.SetIn(originalIn);
                Console.SetOut(originalOut);
                Console.SetError(originalError);
                loadContext?.Unload();
            }

            Write(response);
        }
        catch (Exception ex)
        {
            Write(new ExecResponse { Ok = false, Error = ex.Message });
        }
    }

    private static void Write(ExecResponse response)
    {
        Console.Out.Write(JsonSerializer.Serialize(response, JsonOpts));
        Console.Out.Flush();
        // Do not let submission-created foreground threads outlive the protocol response.
        Environment.Exit(response.Ok ? 0 : 1);
    }
}
