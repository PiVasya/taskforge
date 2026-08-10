using Runner.Services;

var compiler = new RoslynCompilationService();

ExpectAccepted(
    "hello-world",
    """
    using System;
    Console.WriteLine("Hello");
    """);

ExpectAccepted(
    "anonymous-type",
    """
    using System;
    var value = new { Name = "TaskForge", Number = 42 };
    Console.WriteLine($"{value.Name}:{value.Number}");
    """);

ExpectAccepted(
    "async-top-level",
    """
    using System;
    using System.Threading.Tasks;
    await Task.Yield();
    Console.WriteLine("ok");
    """);

ExpectAccepted(
    "iterator",
    """
    using System;
    using System.Collections.Generic;

    foreach (var value in Values())
    {
        Console.WriteLine(value);
    }

    static IEnumerable<int> Values()
    {
        yield return 1;
        yield return 2;
    }
    """);

ExpectRejected(
    "process",
    """
    using System.Diagnostics;
    Process.Start("sh");
    """);

ExpectRejected(
    "filesystem",
    """
    using System.IO;
    Console.WriteLine(File.ReadAllText("/etc/passwd"));
    """);

ExpectRejected(
    "environment-exit",
    """
    using System;
    Environment.Exit(0);
    """);

ExpectRejected(
    "explicit-debug-attribute",
    """
    using System;
    using System.Diagnostics;

    [assembly: Debuggable(DebuggableAttribute.DebuggingModes.Default)]
    Console.WriteLine("blocked");
    """);

ExpectCompileError(
    "ordinary-syntax-error",
    """
    using System;
    Console.WriteLine(
    """);

Console.WriteLine("[csharp-runner-policy] regression cases ok");
return;

void ExpectAccepted(string name, string code)
{
    var result = compiler.Compile(code);
    if (!result.Ok)
    {
        throw new InvalidOperationException($"expected '{name}' to compile, got: {result.Error}");
    }
}

void ExpectRejected(string name, string code)
{
    var result = compiler.Compile(code);
    if (result.Ok)
    {
        throw new InvalidOperationException($"expected '{name}' to be rejected by the security policy");
    }

    if (result.FailureKind != CompilationFailureKind.PolicyError)
    {
        throw new InvalidOperationException($"expected policy rejection for '{name}', got {result.FailureKind}: {result.Error}");
    }
}

void ExpectCompileError(string name, string code)
{
    var result = compiler.Compile(code);
    if (result.Ok || result.FailureKind != CompilationFailureKind.CompileError)
    {
        throw new InvalidOperationException($"expected compile error for '{name}', got {result.FailureKind}: {result.Error}");
    }
}
