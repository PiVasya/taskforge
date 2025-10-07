// Services/Remote/HttpRunnerCompilerBase.cs
using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using taskforge.Data.Models.DTO;
using taskforge.Services.Interfaces;

namespace taskforge.Services.Remote
{
    /// <summary>Общий HTTP-клиент к раннерам. Реализует ICompiler.</summary>
    public abstract class HttpRunnerCompilerBase : ICompiler
    {
        protected readonly IHttpClientFactory _httpFactory;
        protected readonly IConfiguration _cfg;
        protected readonly string _langKey;

        protected HttpRunnerCompilerBase(IHttpClientFactory httpFactory, IConfiguration cfg, string langKey)
        {
            _httpFactory = httpFactory;
            _cfg = cfg;
            _langKey = langKey;
        }

        /// <summary>URL сервиса раннера (например, http://cpp-runner:8080)</summary>
        protected abstract string BaseUrl { get; }

        /// <summary>Дефолтные лимиты (если не пришли в запросе).</summary>
        protected virtual int DefaultTimeLimitMs =>
            _cfg.GetValue<int?>($"Compilers:{_langKey}:TimeoutMs")
            ?? _cfg.GetValue<int?>($"Compilers__{_langKey}__TimeoutMs")
            ?? 5000;

        protected virtual int DefaultMemoryLimitMb =>
            _cfg.GetValue<int?>($"Compilers:{_langKey}:MemoryLimitMb")
            ?? _cfg.GetValue<int?>($"Compilers__{_langKey}__MemoryLimitMb")
            ?? 256;

        public async Task<CompilerRunResponseDto> CompileAndRunAsync(CompilerRunRequestDto req)
        {
            var client = _httpFactory.CreateClient();
            var url = $"{BaseUrl.TrimEnd('/')}/run";

            // контракт раннера: { code, input }
            var body = new
            {
                code  = req.Code,
                input = req.Input
            };

            var resp = await client.PostAsJsonAsync(url, body);
            resp.EnsureSuccessStatusCode();

            var dto = await resp.Content.ReadFromJsonAsync<RunnerRunResponse>();

            var status = MapStatus(dto?.exitCode ?? 0, dto?.stderr);

            return new CompilerRunResponseDto
            {
                Status        = status,                          // ok | compile_error | runtime_error | time_limit | infrastructure_error
                ExitCode      = dto?.exitCode ?? 0,
                Stdout        = dto?.stdout,
                Stderr        = dto?.stderr,
                CompileStderr = status == "compile_error" ? dto?.stderr : null,
                Message       = status == "time_limit" ? "Time limit exceeded" : null
            };
        }

        public async Task<IList<TestResultDto>> RunTestsAsync(
            string code,
            IList<TestCaseDto> testCases,
            int? timeLimitMs = null,
            int? memoryLimitMb = null)
        {
            var client = _httpFactory.CreateClient();
            var url = $"{BaseUrl.TrimEnd('/')}/run/tests";

            var payload = new
            {
                code,
                tests = (testCases ?? new List<TestCaseDto>()).Select(t => new
                {
                    input          = t.Input,
                    expectedOutput = t.ExpectedOutput
                }).ToList()
            };

            var resp = await client.PostAsJsonAsync(url, payload);
            resp.EnsureSuccessStatusCode();

            var dto = await resp.Content.ReadFromJsonAsync<RunnerTestsResponse>();

            var results = new List<TestResultDto>();
            foreach (var r in dto?.results ?? Enumerable.Empty<RunnerTestItem>())
            {
                // Раннер по тестам не отдаёт stderr/exitCode по каждому прогону — заполним по-умолчанию.
                results.Add(new TestResultDto
                {
                    Input          = r.input,
                    ExpectedOutput = r.expectedOutput,
                    ActualOutput   = r.actualOutput,
                    Passed         = r.passed,
                    Status         = r.passed ? "ok" : "runtime_error",
                    ExitCode       = r.passed ? 0 : 1,
                    Stderr         = null,
                    CompileStderr  = null,
                    Hidden         = false
                });
            }
            return results;
        }

        // ===== локальные типы JSON раннеров =====

        private sealed class RunnerRunResponse
        {
            public string? stdout  { get; set; }
            public string? stderr  { get; set; }
            public int     exitCode{ get; set; }
        }

        private sealed class RunnerTestsResponse
        {
            public List<RunnerTestItem>? results { get; set; }
        }

        private sealed class RunnerTestItem
        {
            public string? input          { get; set; }
            public string? expectedOutput { get; set; }
            public string? actualOutput   { get; set; }
            public bool    passed         { get; set; }
        }

        // ===== helpers =====

        private static string MapStatus(int exitCode, string? stderr)
        {
            if (exitCode == 124) return "time_limit";           // как в cpp-runner
            if (!string.IsNullOrEmpty(stderr) &&
                stderr.StartsWith("Compilation error", StringComparison.OrdinalIgnoreCase))
                return "compile_error";
            if (exitCode != 0) return "runtime_error";
            return "ok";
        }
    }
}
