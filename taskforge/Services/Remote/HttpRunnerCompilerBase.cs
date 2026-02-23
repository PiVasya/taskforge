using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
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

        private static readonly JsonSerializerOptions _jsonOpts = new() { PropertyNameCaseInsensitive = true };

        // Cache policy checks per unique (code + rules) to avoid spamming analyzer on each test.
        private static readonly ConcurrentDictionary<string, Task<AnalyzerResponse?>> _policyCache = new();

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
            // 0) Policy check (code-analyzer) BEFORE hitting the runner.
            var policy = await AnalyzePolicyCachedAsync(req.Code, req.PolicyForbiddenCalls, req.PolicyRequiredCalls);
            if (policy is not null && policy.ok == false)
            {
                var details = BuildPolicyDetails(policy);
                Console.WriteLine($"[Runner:{_langKey}] POLICY_FAIL -> block execution\n{details}");
                return new CompilerRunResponseDto
                {
                    Status = "policy_failed",
                    ExitCode = 2,
                    Stdout = null,
                    Stderr = details,
                    CompileStderr = details,
                    Message = "Код содержит запрещённые конструкции"
                };
            }

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
            // 0) Policy check (code-analyzer) once for the whole submission.
            var policy = await AnalyzePolicyCachedAsync(code, null, null);
            if (policy is not null && policy.ok == false)
            {
                var details = BuildPolicyDetails(policy);
                Console.WriteLine($"[Runner:{_langKey}] POLICY_FAIL (tests) -> block execution\n{details}");

                // Return one failed record per test case to keep the pipeline stable.
                var blocked = new List<TestResultDto>();
                foreach (var t in testCases ?? new List<TestCaseDto>())
                {
                    blocked.Add(new TestResultDto
                    {
                        Input = t.Input ?? "",
                        ExpectedOutput = t.ExpectedOutput ?? "",
                        ActualOutput = "",
                        Passed = false,
                        Status = "policy_failed",
                        ExitCode = 2,
                        Stderr = details,
                        CompileStderr = details,
                        Hidden = t.IsHidden
                    });
                }
                return blocked;
            }

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

        // ===== Policy / code-analyzer =====

        private bool IsPolicyEnabled()
        {
            // supports both appsettings.json (CodeAnalyzer:Enabled) and env (CodeAnalyzer__Enabled)
            return _cfg.GetValue<bool>("CodeAnalyzer:Enabled", false);
        }

        private string? CodeAnalyzerUrl()
        {
            var url = _cfg["CodeAnalyzer:Url"];
            if (string.IsNullOrWhiteSpace(url)) return null;
            return url.TrimEnd('/');
        }

        private int CodeAnalyzerTimeoutSeconds()
            => _cfg.GetValue<int>("CodeAnalyzer:TimeoutSeconds", 6);

        private async Task<AnalyzerResponse?> AnalyzePolicyCachedAsync(string? source, List<string>? forbiddenCalls, List<string>? requiredCalls)
        {
            var key = BuildPolicyCacheKey(source ?? string.Empty, forbiddenCalls, requiredCalls);
            return await _policyCache.GetOrAdd(key, _ => AnalyzePolicyAsync(source, forbiddenCalls, requiredCalls));
        }

        private static string BuildPolicyCacheKey(string src, List<string>? forbiddenCalls, List<string>? requiredCalls)
        {
            // stable key = sha256(code) + '|' + joined rules
            static string Join(List<string>? xs)
                => xs == null ? "" : string.Join("\n", xs.Select(x => (x ?? "").Trim()).Where(x => x.Length > 0));

            var rules = $"F:{Join(forbiddenCalls)}|R:{Join(requiredCalls)}";
            using var sha = SHA256.Create();
            var bytes = Encoding.UTF8.GetBytes(src + "\n---\n" + rules);
            var hash = sha.ComputeHash(bytes);
            return Convert.ToHexString(hash);
        }

        private async Task<AnalyzerResponse?> AnalyzePolicyAsync(string? source, List<string>? forbiddenCalls, List<string>? requiredCalls)
        {
            try
            {
                if (!IsPolicyEnabled())
                {
                    Console.WriteLine($"[Runner:{_langKey}] policy.enabled=false -> skip code-analyzer");
                    return null;
                }

                var baseUrl = CodeAnalyzerUrl();
                if (string.IsNullOrWhiteSpace(baseUrl))
                {
                    Console.WriteLine($"[Runner:{_langKey}] policy.enabled=true but CodeAnalyzer:Url is empty -> skip");
                    return null;
                }

                var src = source ?? "";
                var fcnt = forbiddenCalls?.Count ?? 0;
                var rcnt = requiredCalls?.Count ?? 0;
                Console.WriteLine($"[Runner:{_langKey}] POLICY_CHECK -> POST {baseUrl}/analyze source.len={src.Length} forbidden_calls={fcnt} required_calls={rcnt}");
                if (fcnt > 0) Console.WriteLine($"[Runner:{_langKey}] POLICY_CHECK forbidden.sample='{forbiddenCalls![0]}'");
                if (rcnt > 0) Console.WriteLine($"[Runner:{_langKey}] POLICY_CHECK required.sample='{requiredCalls![0]}'");

                var client = _httpFactory.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(CodeAnalyzerTimeoutSeconds());

                // IMPORTANT: snake_case matches Rust DTO field names
                var payload = new AnalyzerRequest
                {
                    language = _langKey,
                    source = src,
                    extra_forbidden = null,
                    forbidden_calls = (forbiddenCalls != null && forbiddenCalls.Count > 0) ? forbiddenCalls : null,
                    required_calls  = (requiredCalls  != null && requiredCalls.Count  > 0) ? requiredCalls  : null,
                };

                var resp = await client.PostAsJsonAsync($"{baseUrl}/analyze", payload);
                var raw = await resp.Content.ReadAsStringAsync();
                Console.WriteLine($"[Runner:{_langKey}] POLICY_CHECK <- HTTP {(int)resp.StatusCode} {resp.StatusCode}");
                Console.WriteLine($"[Runner:{_langKey}] POLICY_CHECK RAW: {raw}");

                if (!resp.IsSuccessStatusCode)
                {
                    Console.WriteLine($"[Runner:{_langKey}] POLICY_CHECK non-2xx -> allow execution (non-strict mode)");
                    return null;
                }

                return JsonSerializer.Deserialize<AnalyzerResponse>(raw, _jsonOpts);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Runner:{_langKey}] POLICY_CHECK EXCEPTION: {ex.GetType().Name}: {ex.Message}");
                Console.WriteLine(ex);

                // If analyzer is down, we DON'T block execution by default (so prod doesn't die).
                // Later we can add a strict mode per task.
                return null;
            }
        }

        private sealed class AnalyzerRequest
        {
            public string language { get; set; } = "";
            public string source { get; set; } = "";
            public object? extra_forbidden { get; set; }
            public List<string>? forbidden_calls { get; set; }
            public List<string>? required_calls { get; set; }
        }

        private static string BuildPolicyDetails(AnalyzerResponse policy)
        {
            var sb = new StringBuilder();
            sb.AppendLine("[policy_failed] Code Analyzer blocked the submission");
            foreach (var e in policy.errors ?? new List<AnalyzerViolation>())
            {
                sb.AppendLine($"- {e.code}: {e.message} (pattern_id={e.pattern_id ?? "-"})");
            }
            if (policy.hits is not null && policy.hits.Count > 0)
            {
                sb.AppendLine("[hits]");
                foreach (var h in policy.hits.Take(50))
                {
                    sb.AppendLine($"- pos={h.position} needle='{h.needle}' id={h.pattern_id ?? "-"} preview='{h.preview}'");
                }
                if (policy.hits.Count > 50) sb.AppendLine($"... hits truncated ({policy.hits.Count})");
            }
            return sb.ToString();
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

        // ===== JSON models for code-analyzer =====

        private sealed class AnalyzerResponse
        {
            public bool ok { get; set; }
            public List<AnalyzerViolation>? errors { get; set; }
            public List<AnalyzerHit>? hits { get; set; }
        }

        private sealed class AnalyzerViolation
        {
            public string? code { get; set; }
            public string? message { get; set; }
            public string? pattern_id { get; set; }
        }

        private sealed class AnalyzerHit
        {
            public string? pattern_id { get; set; }
            public string? needle { get; set; }
            public int position { get; set; }
            public string? preview { get; set; }
        }
    }
}
