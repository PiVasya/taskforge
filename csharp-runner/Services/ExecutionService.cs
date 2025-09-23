using System.Reflection;

namespace Runner.Services;

public sealed class ExecutionService : IExecutionService
{
    public (bool Ok, string Stdout, string Error) Run(Assembly asm, string input, TimeSpan timeout)
    {
        using var inputReader = new StringReader(input);
        using var outputWriter = new StringWriter();

        var oldIn = Console.In;
        var oldOut = Console.Out;

        try
        {
            Console.SetIn(inputReader);
            Console.SetOut(outputWriter);

            var entry = asm.EntryPoint!;
            var args = entry.GetParameters().Length == 0
                ? null
                : new object[] { Array.Empty<string>() };

            using var cts = new CancellationTokenSource(timeout);
            var task = Task.Run(() => entry.Invoke(null, args), cts.Token);

            if (!task.Wait(timeout))
                return (false, "", "Time limit exceeded");

            return (true, outputWriter.ToString(), "");
        }
        catch (Exception ex)
        {
            return (false, "", ex.InnerException?.Message ?? ex.Message);
        }
        finally
        {
            Console.SetIn(oldIn);
            Console.SetOut(oldOut);
        }
    }
}
