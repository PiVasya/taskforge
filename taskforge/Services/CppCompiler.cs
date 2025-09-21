using taskforge.Data.Models.DTO;
using taskforge.Services.Interfaces;

namespace taskforge.Services.Compilers
{
    public class CppCompiler : ICompiler
    {
        public Task<CompilerRunResponseDto> CompileAndRunAsync(string code, string? input)
        {
            throw new NotImplementedException("C++ compiler is not yet implemented");
        }

        public Task<IList<TestResultDto>> RunTestsAsync(string code, IList<TestCaseDto> testCases)
        {
            throw new NotImplementedException("C++ tests not yet implemented");
        }
    }
}
