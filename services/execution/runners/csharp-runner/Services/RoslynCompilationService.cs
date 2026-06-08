using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Runner.Services;

public interface IRoslynCompilationService
{
    (bool Ok, byte[]? Pe, byte[]? Pdb, string Error) Compile(string code);
}

public sealed class RoslynCompilationService : IRoslynCompilationService
{
    public (bool Ok, byte[]? Pe, byte[]? Pdb, string Error) Compile(string code)
    {
        try
        {
            var syntax = CSharpSyntaxTree.ParseText(code, new CSharpParseOptions(LanguageVersion.CSharp14));

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
                allowUnsafe: false,
                concurrentBuild: true,
                usings: new[]
                {
                    "System", "System.IO", "System.Text", "System.Linq", "System.Collections.Generic"
                });

            var compilation = CSharpCompilation.Create(
                assemblyName: "UserSubmission",
                syntaxTrees: new[] { syntax },
                references: references,
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
                return (false, null, null, errors);
            }

            return (true, peStream.ToArray(), pdbStream.ToArray(), "");
        }
        catch (Exception ex)
        {
            return (false, null, null, ex.Message);
        }
    }
}
