using System.Reflection;

namespace Runner.Services;
public interface ICompilationService
{
    (bool Ok, Assembly? Assembly, string Error) CompileToAssembly(string code);
}
