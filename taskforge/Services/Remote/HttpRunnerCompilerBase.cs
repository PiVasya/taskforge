using System.Net.Http.Json;
using System.Collections.Generic;              // ВАЖНО: иначе будет CS0535 по IList<...>
using Microsoft.Extensions.Configuration;
using taskforge.Data.Models.DTO;
using taskforge.Services.Interfaces;

namespace taskforge.Services.Remote
{
    /// Общая реализация ICompiler, бьёт в http-раннер:
    ///   POST {base}/run        { code, input }
    ///   POST {base}/run/tests  { code, tests: [{input, expectedOutput}] }
    public abstract class HttpRunnerCompilerBase : ICompiler
    {
        private readonly IHttpClientFactory _http;
        private readonly string _baseUrl;
        private readonly TimeSpan _timeout;

        protected HttpRunnerCompilerBase(IHttpClientFactory http, IConfiguration cfg, string langKey)
        {
            _http = http;

            // Compilers:<langKey> из appsettings / переменных окружения
            var section = cfg.GetSection($"Compilers:{langKey}");
            _baseUrl = section.GetValue<string>("Url")?.TrimEnd('/')
                      ?? throw new InvalidOperationException($"Compilers:{langKey}:Url is not set");
            var ms = section.GetValue<int?>("TimeoutMs") ?? 10000;
            _timeout = TimeSpan.FromMilliseconds(ms);
        }

        public async Task<CompilerRunResponseDto> CompileAndRunAsync(string code, string? input)
        {
            var client = _http.CreateClient();
            client.Timeout = _timeout;

            // поддерживаем как base=/run, так и base (на случай, если Url уже с /run)
            var res = await client.PostAsJsonAsync($"{_baseUrl}/run", new { code, input });
            if (res.StatusCode == System.Net.HttpStatusCode.NotFound)
                res = await client.PostAsJsonAsync($"{_baseUrl}", new { code, input });

            if (!res.IsSuccessStatusCode)
                return new CompilerRunResponseDto { Error = $"Runner HTTP {(int)res.StatusCode}" };

            // у раннеров разный кейс полей → нормализуем
            var dict = await res.Content.ReadFromJsonAsync<Dictionary<string, object?>>();
            var output = dict?.GetValueOrDefault("output")?.ToString()
                      ?? dict?.GetValueOrDefault("Stdout")?.ToString();
            var error  = dict?.GetValueOrDefault("error")?.ToString()
                      ?? dict?.GetValueOrDefault("Error")?.ToString();

            return new CompilerRunResponseDto { Output = output, Error = error };
        }

        public async Task<IList<TestResultDto>> RunTestsAsync(string code, IList<TestCaseDto> testCases)
        {
            var client = _http.CreateClient();
            client.Timeout = _timeout;

            var payload = new
            {
                code,
                tests = testCases.Select(t => new { input = t.Input, expectedOutput = t.ExpectedOutput }).ToList()
            };

            var res = await client.PostAsJsonAsync($"{_baseUrl}/run/tests", payload);
            if (!res.IsSuccessStatusCode)
                return new List<TestResultDto> {
                    new() { Passed = false, ActualOutput = $"Runner HTTP {(int)res.StatusCode}" }
                };

            // csharp-runner возвращает { results: [...] }, python/cpp — сразу массив
            try
            {
                var wrap = await res.Content.ReadFromJsonAsync<Dictionary<string, object>>();
                if (wrap != null && wrap.TryGetValue("results", out var boxed)
                    && boxed is System.Text.Json.JsonElement el && el.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    var list = new List<TestResultDto>();
                    foreach (var i in el.EnumerateArray())
                    {
                        list.Add(new TestResultDto
                        {
                            Input          = i.GetProperty("input").GetString(),
                            ExpectedOutput = i.GetProperty("expectedOutput").GetString(),
                            ActualOutput   = i.GetProperty("actualOutput").GetString(),
                            Passed         = i.GetProperty("passed").GetBoolean()
                        });
                    }
                    return list;
                }
            }
            catch { /* упадём на прямой разбор ниже */ }

            var direct = await res.Content.ReadFromJsonAsync<List<TestResultDto>>();
            return direct ?? new List<TestResultDto>();
        }
    }
}
