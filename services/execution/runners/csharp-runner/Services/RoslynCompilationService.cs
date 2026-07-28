using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Runner.Services;

public interface IRoslynCompilationService
{
    (bool Ok, byte[]? Pe, byte[]? Pdb, string Error) Compile(string code, CancellationToken cancellationToken = default);
}

public sealed class RoslynCompilationService : IRoslynCompilationService
{
    public (bool Ok, byte[]? Pe, byte[]? Pdb, string Error) Compile(string code, CancellationToken cancellationToken = default)
    {
        try
        {
            var syntax = CSharpSyntaxTree.ParseText(code, new CSharpParseOptions(LanguageVersion.CSharp14), cancellationToken: cancellationToken);

            var trustedAssemblies = (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? string.Empty)
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

            var references = new List<MetadataReference>();
            foreach (var path in trustedAssemblies.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    references.Add(MetadataReference.CreateFromFile(path));
                }
                catch
                {
                    // A broken framework reference is ignored; Roslyn will report
                    // a normal compilation error if the submission needs it.
                }
            }

            var options = new CSharpCompilationOptions(
                OutputKind.ConsoleApplication,
                optimizationLevel: OptimizationLevel.Release,
                allowUnsafe: false,
                concurrentBuild: true,
                deterministic: true,
                checkOverflow: true,
                usings:
                [
                    "System", "System.Text", "System.Linq", "System.Collections.Generic"
                ]);

            var compilation = CSharpCompilation.Create(
                assemblyName: "UserSubmission",
                syntaxTrees: [syntax],
                references: references,
                options: options);

            var securityError = RoslynSecurityPolicy.Validate(compilation, syntax, cancellationToken);
            if (securityError is not null)
            {
                return (false, null, null, securityError);
            }

            using var peStream = new MemoryStream();
            using var pdbStream = new MemoryStream();

            var emit = compilation.Emit(peStream, pdbStream, cancellationToken: cancellationToken);
            if (!emit.Success)
            {
                var errors = string.Join("\n", emit.Diagnostics
                    .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
                    .Select(diagnostic => diagnostic.ToString()));
                return (false, null, null, errors);
            }

            var pe = peStream.ToArray();
            var metadataSecurityError = ManagedPeSecurityPolicy.Validate(pe);
            if (metadataSecurityError is not null)
            {
                return (false, null, null, metadataSecurityError);
            }

            return (true, pe, pdbStream.ToArray(), "");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (false, null, null, ex.Message);
        }
    }
}
