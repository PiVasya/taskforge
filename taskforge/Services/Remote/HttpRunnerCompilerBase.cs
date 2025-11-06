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

        /// <summary>URL сервиса раннера (например, http://csharp-runner:8080)</summary>
        protected abstract string BaseUrl { get; }

        public async Task<CompilerRunResponseDto> CompileAndRunAsync(CompilerRunRequestDto req)
        {
            var client = _httpFactory.CreateClient();
            var url = $"{BaseUrl.TrimEnd('/')}/run";

            var body = new { code = req.Code, input = req.Input ?? "" };

            Console.WriteLine($"[Runner:{_langKey}] POST {url}");
            Console.WriteLine($"[Runner:{_langKey}] code.len={req.Code?.Length ?? 0}  input.visible={(req.Input ?? "").Replace("\r","\\r").Replace("\n","\\n")}");

            var resp = await client.PostAsJsonAsync(url, body);
            resp.EnsureSuccessStatusCode();

            var raw = await resp.Content.ReadAsStringAsync();
            Console.WriteLine($"[Runner:{_langKey}] HTTP {(int)resp.StatusCode} {resp.StatusCode}");
            Console.WriteLine($"[Runner:{_langKey}] RAW: {raw}");

            var dto = System.Text.Json.JsonSerializer.Deserialize<RunnerRunResponse>(raw,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            // Важно для C#: error содержит текст Roslyn
            var stderr = dto?.stderr;
            if (string.IsNullOrWhiteSpace(stderr) && !string.IsNullOrWhiteSpace(dto?.error))
                stderr = dto!.error;

            // определяем статус по коду возврата и stderr
            var status = MapStatus(dto?.exitCode ?? 0, stderr);

            // Попытаемся извлечь статус и compileStderr, если их прислал раннер
            string? remoteStatus        = dto?.status;
            string? remoteCompileStderr = dto?.compileStderr;
            // Если раннер явно вернул статус — используем его, иначе применяем MapStatus
            var effectiveStatus = !string.IsNullOrWhiteSpace(remoteStatus)
                ? remoteStatus
                : status;
            // Для compile_error сначала берём compileStderr из раннера,
            // а если его нет — используем stderr (как и раньше)
            var effectiveCompileStderr = !string.IsNullOrEmpty(remoteCompileStderr)
                ? remoteCompileStderr
                : (effectiveStatus == "compile_error" ? stderr : null);

            return new CompilerRunResponseDto
            {
                Status        = effectiveStatus,   // ok | compile_error | runtime_error | time_limit | infrastructure_error
                ExitCode      = dto?.exitCode ?? 0,
                Stdout        = dto?.stdout,
                Stderr        = stderr,
                CompileStderr = effectiveCompileStderr,
                Message       = effectiveStatus == "time_limit" ? "Time limit exceeded" : null
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
                    input          = t.Input ?? "",
                    expectedOutput = t.ExpectedOutput ?? ""
                }).ToList()
            };

            Console.WriteLine($"[Runner:{_langKey}] POST {url} (tests={payload.tests.Count})");
            var resp = await client.PostAsJsonAsync(url, payload);
            resp.EnsureSuccessStatusCode();

            var raw = await resp.Content.ReadAsStringAsync();
            Console.WriteLine($"[Runner:{_langKey}] HTTP {(int)resp.StatusCode} {resp.StatusCode}");
            Console.WriteLine($"[Runner:{_langKey}] RAW: {raw}");

            var dto = System.Text.Json.JsonSerializer.Deserialize<RunnerTestsResponse>(raw,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            var results = new List<TestResultDto>();
            foreach (var r in dto?.results ?? Enumerable.Empty<RunnerTestItem>())
            {
                results.Add(new TestResultDto
                {
                    Input          = r.input ?? "",
                    ExpectedOutput = r.expectedOutput ?? "",
                    ActualOutput   = r.actualOutput ?? "",
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

        // ===== JSON-модели раннеров =====

        private sealed class RunnerRunResponse
        {
            public string? stdout   { get; set; }
            public string? stderr   { get; set; }
            public int     exitCode { get; set; }
            public string? error    { get; set; } // текст ошибки Roslyn
            public string? status        { get; set; }    // ok | compile_error | runtime_error | time_limit
            public string? compileStderr { get; set; }    // компиляционные ошибки для C++
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

        private static string MapStatus(int exitCode, string? stderr)
        {
            if (exitCode == 124) return "time_limit";
            if (!string.IsNullOrWhiteSpace(stderr))
            {
                var s = stderr.Trim();
                if (s.StartsWith("(") && s.Contains(": error CS")) return "compile_error";
                if (s.Contains("Compilation error", StringComparison.OrdinalIgnoreCase)) return "compile_error";
            }
            // Если stderr пуст, но exitCode != 0 — это компиляционная ошибка (например, C++)
            if (exitCode != 0 && string.IsNullOrWhiteSpace(stderr)) return "compile_error";
            if (exitCode != 0) return "runtime_error";
            return "ok";
        }
    }
}
