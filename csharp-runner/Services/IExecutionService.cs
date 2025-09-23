using System.Reflection;

namespace Runner.Services;
public interface IExecutionService
{
    (bool Ok, string Stdout, string Error) Run(Assembly asm, string input, TimeSpan timeout);
}
