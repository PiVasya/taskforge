using taskforge.Data.Models.DTO;
using taskforge.Services.Interfaces;

namespace taskforge.Services
{
    public sealed class CompilerService : ICompilerService
    {
        private readonly ICompilerProvider _provider;

        public CompilerService(ICompilerProvider provider)
        {
            _provider = provider;
        }

        public async Task<CompilerRunResponseDto> CompileAndRunAsync(CompilerRunRequestDto req)
        {
            // выбираем нужный раннер по языку
            var compiler = _provider.GetCompiler(req.Language);

            var run = await compiler.CompileAndRunAsync(req);

            // аккуратные человекочитаемые сообщения для UI
            if (run.Status == "compile_error" && string.IsNullOrEmpty(run.Message))
                run.Message = "Ошибка компиляции";
            else if (run.Status == "runtime_error" && string.IsNullOrEmpty(run.Message))
                run.Message = "Ошибка во время выполнения";
            else if (run.Status == "time_limit" && string.IsNullOrEmpty(run.Message))
                run.Message = "Превышен лимит времени";

            return run;
        }

        public Task<IList<TestResultDto>> RunTestsAsync(TestRunRequestDto req)
        {
            var compiler = _provider.GetCompiler(req.Language);
            return compiler.RunTestsAsync(
                req.Code,
                req.TestCases ?? new List<TestCaseDto>(),
                req.TimeLimitMs,
                req.MemoryLimitMb
            );
        }
    }
}
