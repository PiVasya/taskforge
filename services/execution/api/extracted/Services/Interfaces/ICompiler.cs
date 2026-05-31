using taskforge.Data.Models.DTO;

namespace taskforge.Services.Interfaces
{
    public interface ICompiler
    {
        /// <summary>
        /// Компиляция и запуск одного кейса.
        /// </summary>
        Task<CompilerRunResponseDto> CompileAndRunAsync(CompilerRunRequestDto req);

        /// <summary>
        /// Запуск набора тестов (с опциональными лимитами времени/памяти).
        /// </summary>
        Task<IList<TestResultDto>> RunTestsAsync(
            string code,
            IList<TestCaseDto> testCases,
            int? timeLimitMs = null,
            int? memoryLimitMb = null
        );
    }
}
