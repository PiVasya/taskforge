namespace Runner.Services;

public interface IExecutionService
{
    Task<(bool Ok, string Stdout, string Error, string Status)> RunAsync(
        byte[] pe,
        byte[] pdb,
        string input,
        int timeLimitMs,
        int memoryLimitMb,
        CancellationToken cancellationToken);
}
