using taskforge.Data.Models.DTO;

namespace taskforge.Services.Interfaces
{
    public interface ICompiler
    {
        Task<CompilerRunResponseDto> CompileAndRunAsync(string code, string? input);
        Task<IList<TestResultDto>> RunTestsAsync(string code, IList<TestCaseDto> testCases);
    }
}
