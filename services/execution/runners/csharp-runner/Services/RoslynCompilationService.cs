using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System.Text;

namespace Runner.Services;

public enum CompilationFailureKind
{
    None,
    CompileError,
    PolicyError,
    InfrastructureError
}

public readonly record struct RoslynCompilationResult(
    bool Ok,
    byte[]? Pe,
    byte[]? Pdb,
    string Error,
    CompilationFailureKind FailureKind)
{
    public static RoslynCompilationResult Success(byte[] pe, byte[] pdb)
        => new(true, pe, pdb, string.Empty, CompilationFailureKind.None);

    public static RoslynCompilationResult CompilationError(string error)
        => new(false, null, null, error, CompilationFailureKind.CompileError);

    public static RoslynCompilationResult PolicyError(string error)
        => new(false, null, null, error, CompilationFailureKind.PolicyError);

    public static RoslynCompilationResult InfrastructureError(string error)
        => new(false, null, null, error, CompilationFailureKind.InfrastructureError);
}

public interface IRoslynCompilationService
{
    RoslynCompilationResult Compile(string code, CancellationToken cancellationToken = default);
}

public sealed class RoslynCompilationService : IRoslynCompilationService
{
    // MetadataReference.CreateFromFile is intentionally done once for the lifetime of the
    // singleton compiler service. Creating the complete trusted-platform reference set for
    // every submission leaves a large amount of mapped metadata/file handles waiting for GC.
    // Under a fast stream of C# submissions this used to exhaust the runner's parent process
    // (EMFILE / "Too many open files") and eventually its container memory.
    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.CSharp14);

    // Direct Roslyn compilation does not read <ImplicitUsings> from the runner's csproj.
    // CSharpCompilationOptions.Usings is not a substitute for SDK-generated global-usings
    // in a normal compilation, so bare student code such as Console.WriteLine(...) used to
    // fail with CS0103. Keep the intended OJ defaults in one trusted synthetic syntax tree
    // and reuse it across submissions. Global usings apply to every user compilation unit,
    // while the security policy still validates the original user syntax tree.
    private static readonly SyntaxTree ImplicitUsingsSyntaxTree = CSharpSyntaxTree.ParseText(
        """
        global using System;
        global using System.Text;
        global using System.Linq;
        global using System.Collections.Generic;
        """,
        options: ParseOptions,
        path: "TaskForge.ImplicitUsings.g.cs",
        encoding: Encoding.UTF8);

    private readonly MetadataReference[] _frameworkReferences = CreateFrameworkReferences();

    public RoslynCompilationResult Compile(string code, CancellationToken cancellationToken = default)
    {
        try
        {
            var syntax = CSharpSyntaxTree.ParseText(
                code,
                options: ParseOptions,
                path: "UserSubmission.cs",
                encoding: Encoding.UTF8,
                cancellationToken: cancellationToken);

            var options = new CSharpCompilationOptions(
                OutputKind.ConsoleApplication,
                optimizationLevel: OptimizationLevel.Release,
                allowUnsafe: false,
                // The runner serializes compilation/execution through RunnerJobGate. Parallel
                // Roslyn compilation only increases peak worker/thread memory here.
                concurrentBuild: false,
                deterministic: true,
                checkOverflow: true);

            var compilation = CSharpCompilation.Create(
                assemblyName: "UserSubmission",
                syntaxTrees: [ImplicitUsingsSyntaxTree, syntax],
                references: _frameworkReferences,
                options: options);

            var securityError = RoslynSecurityPolicy.Validate(compilation, syntax, cancellationToken);
            if (securityError is not null)
            {
                return RoslynCompilationResult.PolicyError(securityError);
            }

            using var peStream = new MemoryStream();
            using var pdbStream = new MemoryStream();

            var emit = compilation.Emit(peStream, pdbStream, cancellationToken: cancellationToken);
            if (!emit.Success)
            {
                var errors = string.Join("\n", emit.Diagnostics
                    .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
                    .Select(diagnostic => diagnostic.ToString()));
                return RoslynCompilationResult.CompilationError(errors);
            }

            var pe = peStream.ToArray();
            var metadataSecurityError = ManagedPeSecurityPolicy.Validate(pe);
            if (metadataSecurityError is not null)
            {
                return RoslynCompilationResult.PolicyError(metadataSecurityError);
            }

            return RoslynCompilationResult.Success(pe, pdbStream.ToArray());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (OutOfMemoryException ex)
        {
            return RoslynCompilationResult.InfrastructureError(ex.Message);
        }
        catch (Exception ex) when (IsInfrastructureFailure(ex))
        {
            return RoslynCompilationResult.InfrastructureError(ex.Message);
        }
        catch (Exception ex)
        {
            return RoslynCompilationResult.CompilationError(ex.Message);
        }
    }


    private static bool IsInfrastructureFailure(Exception exception)
    {
        var message = exception.Message;
        if (!string.IsNullOrWhiteSpace(message)
            && (message.Contains("Too many open files", StringComparison.OrdinalIgnoreCase)
                || message.Contains("EMFILE", StringComparison.OrdinalIgnoreCase)
                || message.Contains("ENFILE", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return exception.InnerException is not null && IsInfrastructureFailure(exception.InnerException);
    }

    private static MetadataReference[] CreateFrameworkReferences()
    {
        var trustedAssemblies = (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

        var references = new List<MetadataReference>();
        foreach (var path in trustedAssemblies.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                references.Add(MetadataReference.CreateFromFile(path));
            }
            catch (Exception ex)
            {
                if (ex is OutOfMemoryException || IsInfrastructureFailure(ex))
                {
                    throw;
                }

                if (ex is not IOException
                    && ex is not UnauthorizedAccessException
                    && ex is not BadImageFormatException
                    && ex is not ArgumentException)
                {
                    throw;
                }

                // Ignore only a genuinely unusable framework entry. Resource exhaustion
                // must fail service initialization instead of producing incomplete refs
                // and misleading student CompileError verdicts later.
            }
        }

        if (references.Count == 0)
        {
            throw new InvalidOperationException("No trusted platform assemblies are available for C# compilation.");
        }

        return references.ToArray();
    }
}
