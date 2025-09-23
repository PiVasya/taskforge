using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System.Reflection;

var app = WebApplication.CreateBuilder(args).Build();



app.MapPost("/run", (RunReq req) =>
{
    var (ok, asm, err) = Compile(req.code);
    if (!ok) return Results.Json(new { error = err });
    var (ran, stdout, ex) = Run(asm!, req.input ?? "");
    return Results.Json(ran ? new { stdout, stderr = "", exitCode = 0 }
                            : new { stdout = "", stderr = ex, exitCode = 1 });
});

app.MapPost("/run/tests", (TestsReq req) =>
{
    var (ok, asm, err) = Compile(req.code);
    if (!ok) return Results.Json(new[] { new { input = "", expectedOutput = "", actualOutput = err, passed = false } });
    var res = new List<object>();
    foreach (var t in req.tests)
    {
        var (ran, stdout, ex) = Run(asm!, t.input ?? "");
        res.Add(new
        {
            input = t.input,
            expectedOutput = t.expectedOutput,
            actualOutput = ran ? stdout : ex,
            passed = ran && stdout == (t.expectedOutput ?? "")
        });
    }
    return Results.Json(res);
});

app.Run();

record RunReq(string code, string? input);
record TestsReq(string code, List<TestCase> tests);
record TestCase(string? input, string? expectedOutput);
static (bool, Assembly?, string) Compile(string code)
{
    var tree = CSharpSyntaxTree.ParseText(code);
    var refs = new[] {
        typeof(object).Assembly.Location,
        typeof(Console).Assembly.Location,
        typeof(Enumerable).Assembly.Location
    }.Select(MetadataReference.CreateFromFile);
    var comp = CSharpCompilation.Create("UserProg", new[] { tree }, refs,
        new CSharpCompilationOptions(OutputKind.ConsoleApplication));
    using var ms = new MemoryStream();
    var result = comp.Emit(ms);
    if (!result.Success)
        return (false, null, string.Join("\n", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
    ms.Position = 0;
    return (true, Assembly.Load(ms.ToArray()), "");
}

static (bool, string, string) Run(Assembly asm, string input)
{
    using var si = new StringReader(input);
    using var so = new StringWriter();
    var oldIn = Console.In; var oldOut = Console.Out;
    try
    {
        Console.SetIn(si); Console.SetOut(so);
        asm.EntryPoint!.Invoke(null, asm.EntryPoint!.GetParameters().Length == 0 ? null : new object[] { Array.Empty<string>() });
        return (true, so.ToString(), "");
    }
    catch (Exception ex)
    {
        return (false, "", ex.Message);
    }
    finally
    {
        Console.SetIn(oldIn); Console.SetOut(oldOut);
    }
}
