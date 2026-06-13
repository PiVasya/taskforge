using Runner.Services;
using System.Text.Json;
using System.Runtime.Loader;
using System.Reflection;

const string ExecArg = "--exec";

if (args.Any(a => string.Equals(a, ExecArg, StringComparison.OrdinalIgnoreCase)))
{
    // Режим “исполнитель” (child-process). Читает JSON из stdin и пишет JSON в stdout.
    ExecMode.Run();
    return;
}

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddTaskForgeDebugDiagnostics("csharp-runner");
builder.Services.AddTaskForgeRedisCache(builder.Configuration, "csharp-runner");

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddSingleton<IRoslynCompilationService, RoslynCompilationService>();
builder.Services.AddSingleton<IExecutionService, ExecutionService>();

var app = builder.Build();

app.UseTaskForgeDebugRequestLogging("csharp-runner");
app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "csharp-runner" }));
app.MapControllers();
app.Run();

static class ExecMode
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
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
            var req = JsonSerializer.Deserialize<ExecRequest>(json, JsonOpts);

            if (req == null || string.IsNullOrWhiteSpace(req.AssemblyBase64))
            {
                Write(new ExecResponse { Ok = false, Error = "Bad exec request." });
                return;
            }

            var pe = Convert.FromBase64String(req.AssemblyBase64);
            var pdb = string.IsNullOrWhiteSpace(req.PdbBase64)
                ? Array.Empty<byte>()
                : Convert.FromBase64String(req.PdbBase64);

            // Нормализуем input
            var normalizedInput = string.IsNullOrEmpty(req.Input) ? "\n" : req.Input!;

            var oldIn = Console.In;
            var oldOut = Console.Out;
            var oldErr = Console.Error;

            using var inputReader = new StringReader(normalizedInput);
            using var outputWriter = new StringWriter();
            using var errorWriter = new StringWriter();

            Console.SetIn(inputReader);
            Console.SetOut(outputWriter);
            Console.SetError(errorWriter);

            var resp = new ExecResponse();

            try
            {
                // Загружаем сборку в отдельный ALC (внутри процесса)
                var alc = new AssemblyLoadContext("user-submission", isCollectible: true);
                using var peStream = new MemoryStream(pe);
                using var pdbStream = pdb.Length > 0 ? new MemoryStream(pdb) : null;

                var asm = pdbStream != null
                    ? alc.LoadFromStream(peStream, pdbStream)
                    : alc.LoadFromStream(peStream);

                var entry = asm.EntryPoint;
                if (entry == null)
                {
                    resp.Ok = false;
                    resp.Error = "Entry point not found.";
                    resp.Stdout = "";
                    Write(resp);
                    return;
                }

                var parameters = entry.GetParameters();
                object? invokeResult;

                if (parameters.Length == 0)
                    invokeResult = entry.Invoke(null, null);
                else
                    invokeResult = entry.Invoke(null, new object[] { Array.Empty<string>() });

                // Если Main async — дожидаемся
                if (invokeResult is Task t)
                    t.GetAwaiter().GetResult();

                resp.Ok = true;
                resp.Stdout = outputWriter.ToString();
                resp.Error = "";
            }
            catch (Exception ex)
            {
                resp.Ok = false;
                resp.Stdout = outputWriter.ToString();
                resp.Error = ex.InnerException?.Message ?? ex.Message;
            }
            finally
            {
                Console.SetIn(oldIn);
                Console.SetOut(oldOut);
                Console.SetError(oldErr);
            }

            Write(resp);
        }
        catch (Exception ex)
        {
            Write(new ExecResponse
            {
                Ok = false,
                Stdout = "",
                Error = ex.Message
            });
        }
    }

    private static void Write(ExecResponse resp)
    {
        var json = JsonSerializer.Serialize(resp, JsonOpts);
        Console.Out.Write(json);
    }
}
