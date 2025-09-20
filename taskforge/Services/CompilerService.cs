using taskforge.Data.Models.DTO;
using taskforge.Services.Interfaces;

namespace taskforge.Services
{
    public class CompilerService : ICompilerService
    {
        private readonly ICompilerProvider _provider;

        public CompilerService(ICompilerProvider provider)
        {
            _provider = provider;
        }

        public async Task<CompilerRunResponseDto> CompileAndRunAsync(CompilerRunRequestDto request)
        {
            var compiler = _provider.GetCompiler(request.Language);
            if (compiler == null)
                return new CompilerRunResponseDto { Error = $"Unsupported language: {request.Language}" };

            return await compiler.CompileAndRunAsync(request.Code, request.Input);
        }

        public async Task<IList<TestResultDto>> RunTestsAsync(TestRunRequestDto request)
        {
            var compiler = _provider.GetCompiler(request.Language);
            if (compiler == null)
                throw new NotSupportedException($"Unsupported language: {request.Language}");

            return await compiler.RunTestsAsync(request.Code, request.TestCases);
        }
    }
}
