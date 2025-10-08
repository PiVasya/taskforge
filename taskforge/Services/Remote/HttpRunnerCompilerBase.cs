// Services/Remote/HttpRunnerCompilerBase.cs
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using taskforge.Data.Models.DTO;
using taskforge.Services.Interfaces;

namespace taskforge.Services.Remote
{
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

        protected abstract string BaseUrl { get; }

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

            var body = new { code = req.Code, input = req.Input };

            Console.WriteLine($"[Runner:{_langKey}] POST {url}");
            Console.WriteLine($"[Runner:{_langKey}] code.len={req.Code?.Length ?? 0}  input.visible={ToVisible(req.Input)}");

            HttpResponseMessage resp;
            try { resp = await client.PostAsJsonAsync(url, body); }
            catch (Exception ex)
            {
                Console.WriteLine($"[Runner:{_langKey}] HTTP ERROR: {ex.GetType().Name} {ex.Message}");
                throw;
            }

            Console.WriteLine($"[Runner:{_langKey}] HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}");
            var json = await resp.Content.ReadAsStringAsync();
            Console.WriteLine($"[Runner:{_langKey}] RAW: {json}");

            resp.EnsureSuccessStatusCode();

            // tolerant parse
            string? stdout = null, stderr = null;
            int exitCode = 0;

            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
                var root = doc.RootElement;

                stdout = JStr(root, "stdout", "output", "Stdout", "Output");
                // важное место: многие раннеры кладут компиляционные ошибки в `error`
                stderr = JStr(root, "stderr", "error", "Stderr", "Error");

                if (!JInt(root, out exitCode, "exitCode", "code", "exit_code"))
                {
                    // success/ok → 0
                    var ok = JBool(root, "success", "ok");
                    exitCode = ok ? 0 : 1;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Runner:{_langKey}] JSON PARSE ERROR: {ex.GetType().Name} {ex.Message}");
                var dto = JsonSerializer.Deserialize<RunnerRunResponse>(json);
                stdout = stdout ?? dto?.stdout;
                stderr = stderr ?? dto?.stderr;
                exitCode = exitCode == 0 ? dto?.exitCode ?? 0 : exitCode;
            }

            var status = MapStatus(_langKey, exitCode, stderr);

            Console.WriteLine($"[Runner:{_langKey}] exitCode={exitCode}  stdout.visible={ToVisible(stdout)}  stderr.visible={ToVisible(stderr)}");

            return new CompilerRunResponseDto
            {
                Status        = status,
                ExitCode      = exitCode,
                Stdout        = stdout,
                Stderr        = stderr,
                CompileStderr = status == "compile_error" ? (stderr ?? "") : null,
                Message       = status == "time_limit" ? "Time limit exceeded" : null
            };

            // local helpers
            static string? JStr(JsonElement el, params string[] names)
            {
                foreach (var n in names)
                    if (el.TryGetProperty(n, out var p) && p.ValueKind != JsonValueKind.Null)
                        return p.GetString();
                return null;
            }
            static bool JInt(JsonElement el, out int value, params string[] names)
            {
                foreach (var n in names)
                {
                    if (el.TryGetProperty(n, out var p))
                    {
                        if (p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out value)) return true;
                        if (p.ValueKind == JsonValueKind.String && int.TryParse(p.GetString(), out value)) return true;
                    }
                }
                value = 0; return false;
            }
            static bool JBool(JsonElement el, params string[] names)
            {
                foreach (var n in names)
                    if (el.TryGetProperty(n, out var p))
                    {
                        if (p.ValueKind == JsonValueKind.True) return true;
                        if (p.ValueKind == JsonValueKind.False) return false;
                        if (p.ValueKind == JsonValueKind.String && bool.TryParse(p.GetString(), out var b)) return b;
                    }
                return false;
            }
        }

        public async Task<IList<TestResultDto>> RunTestsAsync(
            string code,
            IList<TestCaseDto> testCases,
            int? timeLimitMs = null,
            int? memoryLimitMb = null)
        {
            // (оставляю как есть — тесты мы гоняем по одному через CompileAndRunAsync в SolutionService)
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

            Console.WriteLine($"[Runner:{_langKey}] POST {url}  code.len={code?.Length ?? 0}  tests={payload.tests.Count}");

            HttpResponseMessage resp;
            try { resp = await client.PostAsJsonAsync(url, payload); }
            catch (Exception ex)
            {
                Console.WriteLine($"[Runner:{_langKey}] HTTP ERROR (tests): {ex.GetType().Name} {ex.Message}");
                throw;
            }

            Console.WriteLine($"[Runner:{_langKey}] HTTP (tests) {(int)resp.StatusCode} {resp.ReasonPhrase}");
            var json = await resp.Content.ReadAsStringAsync();
            Console.WriteLine($"[Runner:{_langKey}] RAW (tests): {json}");

            resp.EnsureSuccessStatusCode();

            // простая модель, если раннер поддерживает такой ответ
            var list = new List<TestResultDto>();
            try
            {
                var dto = JsonSerializer.Deserialize<RunnerTestsResponse>(json);
                if (dto?.results != null)
                {
                    int idx = 0;
                    foreach (var r in dto.results)
                    {
                        idx++;
                        Console.WriteLine($"[Runner:{_langKey}] test#{idx} passed={r.passed}  exp.visible={ToVisible(r.expectedOutput)}  act.visible={ToVisible(r.actualOutput)}");

                        list.Add(new TestResultDto
                        {
                            Input          = r.input,
                            ExpectedOutput = r.expectedOutput,
                            ActualOutput   = r.actualOutput,
                            Passed         = r.passed,
                            Status         = r.passed ? "ok" : "runtime_error",
                            ExitCode       = r.passed ? 0 : 1,
                            Hidden         = false
                        });
                    }
                }
            }
            catch { /* ok */ }

            return list;
        }

        private sealed class RunnerRunResponse { public string? stdout { get; set; } public string? stderr { get; set; } public int exitCode { get; set; } }
        private sealed class RunnerTestsResponse { public List<RunnerTestItem>? results { get; set; } }
        private sealed class RunnerTestItem { public string? input { get; set; } public string? expectedOutput { get; set; } public string? actualOutput { get; set; } public bool passed { get; set; } }

        private static string MapStatus(string lang, int exitCode, string? stderr)
        {
            if (exitCode == 124) return "time_limit";
            // C# (Roslyn) — часто просто пишет "error CSxxxx" в error/stderr
            if (string.Equals(lang, "csharp", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrEmpty(stderr)
                && stderr.Contains(" error CS", StringComparison.Ordinal))
                return "compile_error";

            if (!string.IsNullOrEmpty(stderr) &&
                stderr.StartsWith("Compilation error", StringComparison.OrdinalIgnoreCase))
                return "compile_error";

            if (exitCode != 0) return "runtime_error";
            return "ok";
        }

        private static string ToVisible(string? s)
        {
            if (s == null) return "⟨null⟩";
            return s.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
        }
    }
}
