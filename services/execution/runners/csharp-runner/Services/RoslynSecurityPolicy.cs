using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Runner.Services;

internal static class RoslynSecurityPolicy
{
    private static readonly string[] ForbiddenNamespacePrefixes =
    [
        "System.Diagnostics",
        "System.IO",
        "System.Net",
        "System.Reflection",
        "System.Runtime.InteropServices",
        "System.Runtime.Loader",
        "System.Linq.Expressions",
        "System.CodeDom",
        "System.Security",
        "System.Management",
        "System.DirectoryServices",
        "Microsoft.Win32",
        "Microsoft.CSharp.RuntimeBinder"
    ];

    private static readonly string[] ForbiddenTypeNames =
    [
        "System.Environment",
        "System.AppDomain",
        "System.Activator",
        "System.Type",
        "System.Delegate",
        "System.Threading.Thread",
        "System.Threading.ThreadPool",
        "System.Runtime.CompilerServices.RuntimeHelpers",
        "System.Runtime.CompilerServices.Unsafe",
        "System.Runtime.CompilerServices.UnmanagedCallersOnlyAttribute",
        "System.Runtime.CompilerServices.ModuleInitializerAttribute"
    ];

    internal static string? Validate(
        CSharpCompilation compilation,
        SyntaxTree syntaxTree,
        CancellationToken cancellationToken)
    {
        var root = syntaxTree.GetRoot(cancellationToken);

        foreach (var directive in root
                     .DescendantTrivia(descendIntoTrivia: true)
                     .Select(trivia => trivia.GetStructure())
                     .OfType<DirectiveTriviaSyntax>())
        {
            if (directive.IsKind(SyntaxKind.ReferenceDirectiveTrivia)
                || directive.IsKind(SyntaxKind.LoadDirectiveTrivia)
                || directive.IsKind(SyntaxKind.LineDirectiveTrivia)
                || directive.IsKind(SyntaxKind.PragmaChecksumDirectiveTrivia))
            {
                return "Security policy: this preprocessor directive is not available in the OJ.";
            }
        }

        foreach (var node in root.DescendantNodesAndSelf())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (node is UnsafeStatementSyntax
                or StackAllocArrayCreationExpressionSyntax
                or ImplicitStackAllocArrayCreationExpressionSyntax
                or PointerTypeSyntax
                or FunctionPointerTypeSyntax
                or SizeOfExpressionSyntax
                or FixedStatementSyntax
                or RefTypeExpressionSyntax
                or RefValueExpressionSyntax
                or MakeRefExpressionSyntax)
            {
                return "Security policy: unsafe and low-level memory constructs are not available in the OJ.";
            }

            if (node is IdentifierNameSyntax identifier
                && string.Equals(identifier.Identifier.ValueText, "dynamic", StringComparison.Ordinal))
            {
                return "Security policy: dynamic binding is not available in the OJ.";
            }

            if (node is MethodDeclarationSyntax method
                && method.Modifiers.Any(SyntaxKind.ExternKeyword))
            {
                return "Security policy: external methods are not available in the OJ.";
            }

            if (node is AttributeSyntax attribute)
            {
                var attributeName = attribute.Name.ToString();
                if (ContainsForbiddenAttributeName(attributeName))
                {
                    return "Security policy: native and early-execution attributes are not available in the OJ.";
                }
            }
        }

        var model = compilation.GetSemanticModel(syntaxTree, ignoreAccessibility: true);
        foreach (var node in root.DescendantNodesAndSelf())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (node is not ExpressionSyntax
                && node is not TypeSyntax
                && node is not UsingDirectiveSyntax
                && node is not AttributeSyntax)
            {
                continue;
            }

            var info = model.GetSymbolInfo(node, cancellationToken);
            var symbol = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
            if (symbol is not null && IsForbidden(symbol))
            {
                return "Security policy: this framework API is not available in the OJ.";
            }

            if (node is ExpressionSyntax or TypeSyntax or AttributeSyntax)
            {
                var typeInfo = model.GetTypeInfo(node, cancellationToken);
                if ((typeInfo.Type is not null && IsForbidden(typeInfo.Type))
                    || (typeInfo.ConvertedType is not null && IsForbidden(typeInfo.ConvertedType)))
                {
                    return "Security policy: this framework API is not available in the OJ.";
                }
            }
        }

        return null;
    }

    private static bool ContainsForbiddenAttributeName(string name)
        => name.Contains("DllImport", StringComparison.OrdinalIgnoreCase)
            || name.Contains("LibraryImport", StringComparison.OrdinalIgnoreCase)
            || name.Contains("UnmanagedCallersOnly", StringComparison.OrdinalIgnoreCase)
            || name.Contains("ModuleInitializer", StringComparison.OrdinalIgnoreCase)
            || name.Contains("SuppressUnmanagedCodeSecurity", StringComparison.OrdinalIgnoreCase);

    private static bool IsForbidden(ISymbol symbol)
    {
        var type = symbol as INamedTypeSymbol ?? symbol.ContainingType;
        if (type is not null)
        {
            var typeName = type.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
            if (ForbiddenTypeNames.Any(value => string.Equals(value, typeName, StringComparison.Ordinal)))
            {
                return true;
            }
        }

        var ns = symbol.ContainingNamespace?.ToDisplayString() ?? string.Empty;
        if (ForbiddenNamespacePrefixes.Any(prefix =>
                string.Equals(ns, prefix, StringComparison.Ordinal)
                || ns.StartsWith(prefix + ".", StringComparison.Ordinal)))
        {
            return true;
        }

        if (symbol is IMethodSymbol method)
        {
            var containingType = method.ContainingType?.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) ?? string.Empty;
            if (string.Equals(containingType, "System.Console", StringComparison.Ordinal)
                && method.Name.StartsWith("OpenStandard", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
