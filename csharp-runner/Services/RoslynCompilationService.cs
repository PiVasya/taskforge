using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System.Reflection;

namespace Runner.Services;

public sealed class RoslynCompilationService : ICompilationService
{
    private static readonly CSharpCompilationOptions Options =
        new(OutputKind.ConsoleApplication,
            optimizationLevel: OptimizationLevel.Release,
            concurrentBuild: true);

    private static readonly MetadataReference[] Refs =
    {
        MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(Console).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(Enumerable).Assembly.Location),
        // при необходимости добавляй сюда другие референсы (System.Runtime и т.п.)
    };

    public (bool Ok, Assembly? Assembly, string Error) CompileToAssembly(string code)
    {
        var tree = CSharpSyntaxTree.ParseText(code);
        var compilation = CSharpCompilation.Create(
            assemblyName: "UserProgram",
            syntaxTrees: new[] { tree },
            references: Refs,
            options: Options
        );

        using var ms = new MemoryStream();
        var emit = compilation.Emit(ms);
        if (!emit.Success)
        {
            var err = string.Join("\n", emit.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => d.ToString()));
            return (false, null, err);
        }

        ms.Position = 0;
        return (true, Assembly.Load(ms.ToArray()), "");
    }
}
