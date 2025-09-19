using taskforge.Data.Models.DTO;

namespace taskforge.Services.Interfaces
{
    public interface ICompilerService
    {
        Task<CompilerRunResponseDto> CompileAndRunAsync(CompilerRunRequestDto request);
        Task<IList<TestResultDto>> RunTestsAsync(TestRunRequestDto request);
    }
}
