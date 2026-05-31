using taskforge.Data.Models.DTO;

namespace taskforge.Services.Interfaces
{
    public interface ICompilerService
    {
        Task<CompilerRunResponseDto> CompileAndRunAsync(CompilerRunRequestDto req);
        Task<IList<TestResultDto>> RunTestsAsync(TestRunRequestDto req);
    }
}
