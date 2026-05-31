namespace Runner.Services;

public interface IExecutionService
{
    (bool Ok, string Stdout, string Error) Run(byte[] pe, byte[] pdb, string input, TimeSpan timeout);
}
