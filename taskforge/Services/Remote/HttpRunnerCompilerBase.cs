using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using taskforge.Data.Models.DTO;
using taskforge.Services.Interfaces;
namespace taskforge.Services.Remote
{
    /// Общая реализация ICompiler, которая бьёт в http-раннер вида:
    ///  POST {baseUrl}/run        { code, input }
    ///  POST {baseUrl}/run-tests  { code, tests: [{input, expectedOutput}] }
    public abstract class HttpRunnerCompilerBase : ICompiler
    {
        private readonly IHttpClientFactory _http;
        private readonly string _baseUrl;
        private readonly TimeSpan _timeout;

        protected HttpRunnerCompilerBase(IHttpClientFactory http, IConfiguration cfg, string langKey)
        {
            _http = http;

            // читаем Compilers:<langKey> из конфигурации (env/compose тоже попадут сюда)
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

            var res = await client.PostAsJsonAsync($"{_baseUrl}", new { code, input });
            // раннеры слушают /run — но compose кладёт полный URL; поддержим оба варианта:
            if (res.StatusCode == System.Net.HttpStatusCode.NotFound)
                res = await client.PostAsJsonAsync($"{_baseUrl}/run", new { code, input });

            if (!res.IsSuccessStatusCode)
                return new CompilerRunResponseDto { Error = $"Runner HTTP { (int)res.StatusCode }" };

            // ожидаем { output?, error? } либо { ExitCode, Stdout, Error } — сведём к общему виду
            var json = await res.Content.ReadFromJsonAsync<Dictionary<string, object?>>();
            var outText = json?.GetValueOrDefault("output")?.ToString()
                       ?? json?.GetValueOrDefault("Stdout")?.ToString();
            var errText = json?.GetValueOrDefault("error")?.ToString()
                       ?? json?.GetValueOrDefault("Error")?.ToString();

            return new CompilerRunResponseDto { Output = outText, Error = errText };
        }

        public async Task<IList[TestResultDto>> RunTestsAsync(string code, IList<TestCaseDto> testCases)
        {
            var client = _http.CreateClient();
            client.Timeout = _timeout;

            // нашим раннерам нужен ключ "tests"
            var payload = new
            {
                code,
                tests = testCases.Select(t => new { input = t.Input, expectedOutput = t.ExpectedOutput }).ToList()
            };

            var res = await client.PostAsJsonAsync($"{_baseUrl}/run-tests", payload);
            if (!res.IsSuccessStatusCode)
                return new List<TestResultDto> {
                    new() { Passed = false, ActualOutput = $"Runner HTTP { (int)res.StatusCode }" }
                };

            // python/cpp раннеры отдают массив [{input, expectedOutput, actualOutput, passed}]
            // csharp отдаёт { results: [...] }
            var dict = await res.Content.ReadFromJsonAsync<Dictionary<string, object>>();
            if (dict != null && dict.TryGetValue("results", out var boxed) && boxed is System.Text.Json.JsonElement el && el.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                var list = new List<TestResultDto>();
                foreach (var i in el.EnumerateArray())
                {
                    list.Add(new TestResultDto
                    {
                        Input = i.GetProperty("input").GetString(),
                        ExpectedOutput = i.GetProperty("expectedOutput").GetString(),
                        ActualOutput = i.GetProperty("actualOutput").GetString(),
                        Passed = i.GetProperty("passed").GetBoolean()
                    });
                }
                return list;
            }

            // иначе — считаем, что вернулся сразу массив
            var arr = await res.Content.ReadFromJsonAsync<List<TestResultDto>>();
            return arr ?? new List<TestResultDto>();
        }
    }
}
