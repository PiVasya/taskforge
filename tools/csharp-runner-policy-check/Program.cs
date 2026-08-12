using Runner.Services;

var compiler = new RoslynCompilationService();

ExpectAccepted(
    "hello-world",
    """
    using System;
    Console.WriteLine("Hello");
    """);

ExpectAccepted(
    "implicit-system-using",
    """
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

ExpectAccepted(
    "record",
    """
    using System;

    var value = new Point(3, 4);
    Console.WriteLine(value);

    public sealed record Point(int X, int Y);
    """);

ExpectAccepted(
    "large-array-initializer",
    """
    using System;
    using System.Linq;

    var values = new[]
    {
        1, 2, 3, 4, 5, 6, 7, 8,
        9, 10, 11, 12, 13, 14, 15, 16,
        17, 18, 19, 20, 21, 22, 23, 24,
        25, 26, 27, 28, 29, 30, 31, 32,
        33, 34, 35, 36, 37, 38, 39, 40,
        41, 42, 43, 44, 45, 46, 47, 48,
        49, 50, 51, 52, 53, 54, 55, 56,
        57, 58, 59, 60, 61, 62, 63, 64
    };
    Console.WriteLine(values.Sum());
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
    "process-alias",
    """
    using P = System.Diagnostics.Process;
    P.Start("sh");
    """);

ExpectRejected(
    "filesystem-fully-qualified",
    """
    using System;
    Console.WriteLine(global::System.IO.File.ReadAllText("/etc/passwd"));
    """);

ExpectRejected(
    "get-type",
    """
    using System;
    var type = new object().GetType();
    Console.WriteLine(type.FullName);
    """);

ExpectRejected(
    "environment-exit",
    """
    using System;
    Environment.Exit(0);
    """);

ExpectRejected(
    "system-type",
    """
    using System;
    Console.WriteLine(typeof(string));
    """);

ExpectRejected(
    "runtime-helpers",
    """
    using System;
    using System.Runtime.CompilerServices;
    Console.WriteLine(RuntimeHelpers.GetHashCode(new object()));
    """);

ExpectRejected(
    "raw-standard-stream",
    """
    using System;
    using var output = Console.OpenStandardOutput();
    Console.WriteLine(output.CanWrite);
    """);

ExpectRejected(
    "explicit-debug-attribute",
    """
    using System;
    using System.Diagnostics;

    [assembly: Debuggable(DebuggableAttribute.DebuggingModes.Default)]
    Console.WriteLine("blocked");
    """);

ExpectRejected(
    "dll-import",
    """
    using System;
    using System.Runtime.InteropServices;

    Console.WriteLine(Native.getpid());

    static class Native
    {
        [DllImport("libc")]
        public static extern int getpid();
    }
    """);

ExpectCompileError(
    "ordinary-syntax-error",
    """
    using System;
    Console.WriteLine(
    """);

ExpectStableCompilerResources();

Console.WriteLine("[csharp-runner-policy] regression cases ok");
return;

void ExpectAccepted(string name, string code)
{
    var result = compiler.Compile(code);
    if (!result.Ok)
    {
        throw new InvalidOperationException($"expected '{name}' to compile, got {result.FailureKind}: {result.Error}");
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
void ExpectStableCompilerResources()
{
    if (!OperatingSystem.IsLinux() || !Directory.Exists("/proc/self/fd"))
    {
        return;
    }

    var before = Directory.GetFiles("/proc/self/fd").Length;
    for (var i = 0; i < 64; i++)
    {
        var result = compiler.Compile(
            "using System; Console.WriteLine(42);");
        if (!result.Ok)
        {
            throw new InvalidOperationException(
                $"resource-stability compile {i} failed with {result.FailureKind}: {result.Error}");
        }
    }

    var after = Directory.GetFiles("/proc/self/fd").Length;
    var growth = after - before;
    if (growth > 32)
    {
        throw new InvalidOperationException(
            $"C# compiler leaked too many parent file descriptors across repeated compiles: before={before}, after={after}, growth={growth}");
    }

    Console.WriteLine($"[csharp-runner-policy] resource stability ok: fd growth {growth}");
}

