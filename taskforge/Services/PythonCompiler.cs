using taskforge.Data.Models.DTO;
using taskforge.Services.Interfaces;

namespace taskforge.Services.Compilers
{
    public class PythonCompiler : ICompiler
    {
        public Task<CompilerRunResponseDto> CompileAndRunAsync(string code, string? input)
        {
            throw new NotImplementedException("Python execution not yet implemented");
        }

        public Task<IList<TestResultDto>> RunTestsAsync(string code, IList<TestCaseDto> testCases)
        {
            throw new NotImplementedException("Python tests not yet implemented");
        }
    }
}
