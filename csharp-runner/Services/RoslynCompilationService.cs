using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System.Reflection;

namespace Runner.Services;

public interface IRoslynCompilationService
{
    (bool Ok, Assembly? Assembly, string Error) Compile(string code);
}

public sealed class RoslynCompilationService : IRoslynCompilationService
{
    public (bool Ok, Assembly? Assembly, string Error) Compile(string code)
    {
        try
        {
            var syntax = CSharpSyntaxTree.ParseText(code, new CSharpParseOptions(LanguageVersion.Preview));

            // ПОЛНЫЙ набор платформенных сборок (TPA) — критично для CS0012/System.Runtime
            var tpa = (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? string.Empty)
                      .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

            var references = tpa
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(p =>
                {
                    try { return MetadataReference.CreateFromFile(p); }
                    catch { return null; }
                })
                .Where(r => r != null)!
                .ToList();

            var options = new CSharpCompilationOptions(
                OutputKind.ConsoleApplication,
                optimizationLevel: OptimizationLevel.Release,
                allowUnsafe: true,
                concurrentBuild: true,
                usings: new[]
                {
                    "System", "System.IO", "System.Text", "System.Linq", "System.Collections.Generic"
                });

            var compilation = CSharpCompilation.Create(
                assemblyName: "UserSubmission",
                syntaxTrees: new[] { syntax },
                references: references!,
                options: options
            );

            using var peStream = new MemoryStream();
            using var pdbStream = new MemoryStream();

            var emit = compilation.Emit(peStream, pdbStream);
            if (!emit.Success)
            {
                var errors = string.Join("\n", emit.Diagnostics
                    .Where(d => d.Severity == DiagnosticSeverity.Error)
                    .Select(d => d.ToString()));
                return (false, null, errors);
            }

            peStream.Position = 0;
            pdbStream.Position = 0;

            var asm = Assembly.Load(peStream.ToArray(), pdbStream.ToArray());
            return (true, asm, "");
        }
        catch (Exception ex)
        {
            return (false, null, ex.Message);
        }
    }
}
