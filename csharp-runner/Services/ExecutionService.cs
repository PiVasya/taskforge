using System.Reflection;

namespace Runner.Services;

public sealed class ExecutionService : IExecutionService
{
    public (bool Ok, string Stdout, string Error) Run(Assembly asm, string input, TimeSpan timeout)
    {
        // если вход пустой – отправляем хотя бы перевод строки (важно для Console.ReadLine)
        var normalized = string.IsNullOrEmpty(input) ? "\n" : input;

        using var inputReader = new StringReader(normalized);
        using var outputWriter = new StringWriter();

        var oldIn = Console.In;
        var oldOut = Console.Out;

        try
        {
            Console.SetIn(inputReader);
            Console.SetOut(outputWriter);

            // ищем статический Main
            var entry = asm.EntryPoint;
            if (entry is null)
                return (false, "", "Entry point not found.");

            var parameters = entry.GetParameters();
            object? result;

            var cts = new CancellationTokenSource(timeout);
            var runTask = Task.Run(() =>
            {
                if (parameters.Length == 0)
                {
                    result = entry.Invoke(null, null);
                }
                else
                {
                    // Main(string[] args)
                    result = entry.Invoke(null, new object[] { Array.Empty<string>() });
                }
            }, cts.Token);

            try
            {
                runTask.Wait(cts.Token);
            }
            catch (OperationCanceledException)
            {
                return (false, "", "Time limit exceeded.");
            }

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
