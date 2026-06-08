using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TaskForge.Execution.Worker;

public sealed class Worker(ILogger<Worker> logger, IHttpClientFactory httpClientFactory, IConfiguration configuration) : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Execution worker started. It claims queued jobs from execution-api and dispatches them to language runners.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var job = await ClaimNextJobAsync(stoppingToken);
                if (job is null)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                    continue;
                }

                await ProcessJobAsync(job, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Execution worker loop failed.");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private async Task<ExecutionJobDto?> ClaimNextJobAsync(CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{ServiceUrl("ExecutionApi", "http://execution-api:8080")}/api/internal/execution/jobs/claim-next");
        AddInternalKey(request);
        using var response = await client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Execution job claim failed with status {StatusCode}.", response.StatusCode);
            return null;
        }

        var payload = await response.Content.ReadFromJsonAsync<ClaimNextResponse>(JsonOptions, ct);
        return payload?.Job;
    }

    private async Task ProcessJobAsync(ExecutionJobDto job, CancellationToken ct)
    {
        var started = Stopwatch.StartNew();
        var tests = ParseTests(job.TestsJson);
        RunnerResult runnerResult;

        if (tests.Length == 0)
        {
            runnerResult = RunnerResult.NoTests();
        }
        else
        {
            var policyBlock = await AnalyzePolicyAsync(job, ct);
            runnerResult = policyBlock ?? await RunTestsAsync(job, tests, ct);
        }

        started.Stop();

        var complete = new CompleteExecutionJobRequest(
            Status: runnerResult.Verdict,
            Stdout: runnerResult.Stdout,
            Stderr: runnerResult.Stderr,
            ExitCode: runnerResult.ExitCode,
            DurationMs: started.ElapsedMilliseconds,
            Score: runnerResult.Score,
            Passed: runnerResult.Passed,
            Result: runnerResult.Raw);

        await CompleteJobAsync(job.Id, complete, ct);
        await PublishVerdictAsync(job.SubmissionId, runnerResult, ct);
    }

    private async Task<RunnerResult?> AnalyzePolicyAsync(ExecutionJobDto job, CancellationToken ct)
    {
        if (!configuration.GetValue("CodeAnalyzer:Enabled", true)) return null;

        var baseUrl = (configuration["CodeAnalyzer:Url"] ?? "http://code-analyzer:8080").TrimEnd('/');
        if (string.IsNullOrWhiteSpace(baseUrl)) return null;

        var forbidden = ParseStringArray(job.CodeForbiddenCallsJson);
        var required = ParseStringArray(job.CodeRequiredCallsJson);
        var client = httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue("CodeAnalyzer:TimeoutSeconds", 8), 2, 60));

        var payload = new AnalyzerRequest(
            Language: NormalizeLanguage(job.Language),
            Source: job.Code ?? string.Empty,
            ExtraForbidden: null,
            ForbiddenCalls: forbidden.Length > 0 ? forbidden : null,
            RequiredCalls: required.Length > 0 ? required : null);

        try
        {
            using var response = await client.PostAsJsonAsync($"{baseUrl}/analyze", payload, JsonOptions, ct);
            var text = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                var failClosed = configuration.GetValue("CodeAnalyzer:FailClosed", true);
                return failClosed
                    ? RunnerResult.Error("JudgeUnavailable", $"Code analyzer returned {(int)response.StatusCode}.", CloneJson(text))
                    : null;
            }

            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
            var root = doc.RootElement.Clone();
            var ok = root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("ok", out var okProp)
                && okProp.ValueKind is JsonValueKind.True or JsonValueKind.False
                && okProp.GetBoolean();

            return ok
                ? null
                : RunnerResult.PolicyFailed(root, BuildPolicyMessage(root));
        }
        catch (Exception ex)
        {
            var failClosed = configuration.GetValue("CodeAnalyzer:FailClosed", true);
            return failClosed
                ? RunnerResult.Error("JudgeUnavailable", "Сервис анализа кода недоступен: " + ex.Message, CloneJson(JsonSerializer.Serialize(new { error = ex.Message }, JsonOptions)))
                : null;
        }
    }

    private async Task<RunnerResult> RunTestsAsync(ExecutionJobDto job, JsonElement[] tests, CancellationToken ct)
    {
        var language = NormalizeLanguage(job.Language);
        var baseUrl = RunnerUrl(language);
        if (baseUrl is null)
        {
            return RunnerResult.Error("JudgeUnavailable", $"Unsupported language: {job.Language}");
        }

        var payload = new RunnerRequest(job.Language, job.Code ?? string.Empty, job.Input, tests, tests, job.TimeLimitMs, job.MemoryLimitMb);
        var client = httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue("Judge:TimeoutSeconds", 45), 5, 180));

        try
        {
            using var response = await client.PostAsJsonAsync($"{baseUrl}/run-tests", payload, JsonOptions, ct);
            var text = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                return RunnerResult.Error("JudgeUnavailable", $"Runner returned {(int)response.StatusCode}.", CloneJson(text));
            }

            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
            var root = doc.RootElement.Clone();
            var results = ExtractResults(root);
            var total = results.HasValue && results.Value.ValueKind == JsonValueKind.Array ? results.Value.GetArrayLength() : 0;
            var passed = results.HasValue && results.Value.ValueKind == JsonValueKind.Array ? results.Value.EnumerateArray().Count(IsPassedResult) : 0;
            var allPassed = total > 0 && passed == total;
            var compileError = IsCompileErrorRoot(root) || (results.HasValue && results.Value.ValueKind == JsonValueKind.Array && results.Value.EnumerateArray().Any(IsCompileErrorResult));
            var verdict = allPassed ? "Accepted" : compileError ? "CompileError" : "Rejected";
            var score = total <= 0 ? 0 : (int)Math.Round(passed * 100.0 / total, MidpointRounding.AwayFromZero);
            return new RunnerResult(verdict, score, allPassed, root, results, null, null, null, compileError);
        }
        catch (Exception ex)
        {
            return RunnerResult.Error("JudgeUnavailable", ex.Message, CloneJson(JsonSerializer.Serialize(new { error = ex.Message }, JsonOptions)));
        }
    }

    private async Task CompleteJobAsync(Guid jobId, CompleteExecutionJobRequest requestBody, CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{ServiceUrl("ExecutionApi", "http://execution-api:8080")}/api/internal/execution/jobs/{jobId}/complete")
        {
            Content = JsonContent.Create(requestBody, options: JsonOptions)
        };
        AddInternalKey(request);
        using var response = await client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Completing execution job {JobId} failed with status {StatusCode}.", jobId, response.StatusCode);
        }
    }

    private async Task PublishVerdictAsync(Guid submissionId, RunnerResult result, CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient();
        var payload = new SolutionVerdictRequest(result.Verdict, result.Score, result.Message, result.ToSolutionJson());
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{ServiceUrl("SolutionsApi", "http://solutions-api:8080")}/api/internal/solutions/submissions/{submissionId}/verdict")
        {
            Content = JsonContent.Create(payload, options: JsonOptions)
        };
        AddInternalKey(request);
        using var response = await client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Publishing verdict for submission {SubmissionId} failed with status {StatusCode}.", submissionId, response.StatusCode);
        }
    }

    private string ServiceUrl(string name, string fallback)
        => (configuration[$"Services:{name}"] ?? configuration[$"ServiceUrls:{name}"] ?? fallback).TrimEnd('/');

    private string? RunnerUrl(string language) => language switch
    {
        "csharp" => configuration["Runners:CSharp"] ?? "http://csharp-runner:8080",
        "cpp" => configuration["Runners:Cpp"] ?? "http://cpp-runner:8080",
        "python" => configuration["Runners:Python"] ?? "http://python-runner:8080",
        "java" => configuration["Runners:Java"] ?? "http://java-runner:8080",
        "javascript" => configuration["Runners:Javascript"] ?? "http://javascript-runner:8080",
        "pascal" => configuration["Runners:Pascal"] ?? "http://pascal-runner:8080",
        _ => null
    };

    private void AddInternalKey(HttpRequestMessage request)
    {
        var key = configuration["InternalApi:Key"] ?? configuration["TaskForge:InternalKey"] ?? Environment.GetEnvironmentVariable("TASKFORGE_INTERNAL_KEY");
        if (!string.IsNullOrWhiteSpace(key)) request.Headers.TryAddWithoutValidation("X-Internal-Key", key);
    }

    private static string NormalizeLanguage(string? lang) => (lang ?? "csharp").Trim().ToLowerInvariant() switch
    {
        "c#" or "cs" or "csharp" => "csharp",
        "c++" or "cpp" or "g++" or "gcc" or "cxx" => "cpp",
        "py" or "python" or "python3" => "python",
        "js" or "javascript" or "node" or "nodejs" or "node.js" => "javascript",
        "java" => "java",
        "pascal" or "pabc" => "pascal",
        var x => x
    };

    private static string[] ParseStringArray(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return [];
            return doc.RootElement.EnumerateArray()
                .Where(x => x.ValueKind == JsonValueKind.String)
                .Select(x => x.GetString() ?? string.Empty)
                .Select(x => x.Trim())
                .Where(x => x.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    private static string BuildPolicyMessage(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("errors", out var errors) || errors.ValueKind != JsonValueKind.Array)
        {
            return "Код содержит запрещённые конструкции.";
        }

        var lines = errors.EnumerateArray()
            .Select(e => e.ValueKind == JsonValueKind.Object && e.TryGetProperty("message", out var m) ? m.ToString() : null)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToArray();

        return lines.Length == 0
            ? "Код содержит запрещённые конструкции."
            : "Код содержит запрещённые конструкции: " + string.Join("; ", lines);
    }

    private static JsonElement[] ParseTests(string? testsJson)
    {
        if (string.IsNullOrWhiteSpace(testsJson)) return [];
        try
        {
            using var doc = JsonDocument.Parse(testsJson);
            return ElementToArray(doc.RootElement);
        }
        catch
        {
            return [];
        }
    }

    private static JsonElement[] ElementToArray(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Array) return element.EnumerateArray().Select(x => x.Clone()).ToArray();
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var name in new[] { "testCases", "tests", "cases" })
            {
                if (element.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.Array)
                {
                    return prop.EnumerateArray().Select(x => x.Clone()).ToArray();
                }
            }
        }
        return [];
    }

    private static JsonElement? ExtractResults(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        foreach (var name in new[] { "results", "testCases", "cases" })
        {
            if (root.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.Array)
            {
                return prop.Clone();
            }
        }
        return null;
    }

    private static bool IsPassedResult(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object) return false;
        if (item.TryGetProperty("passed", out var passed) && passed.ValueKind is JsonValueKind.True or JsonValueKind.False) return passed.GetBoolean();
        if (item.TryGetProperty("status", out var status)) return string.Equals(status.ToString(), "ok", StringComparison.OrdinalIgnoreCase);
        return false;
    }

    private static bool IsCompileErrorResult(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object) return false;
        if (item.TryGetProperty("status", out var status) && string.Equals(status.ToString(), "compile_error", StringComparison.OrdinalIgnoreCase)) return true;
        if (item.TryGetProperty("compileStderr", out var compileStderr) && !string.IsNullOrWhiteSpace(compileStderr.ToString())) return true;
        return false;
    }

    private static bool IsCompileErrorRoot(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return false;
        if (root.TryGetProperty("status", out var status))
        {
            var value = status.ToString();
            if (string.Equals(value, "compile_error", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "compilation_error", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "compileerror", StringComparison.OrdinalIgnoreCase)) return true;
        }
        if (root.TryGetProperty("compileStderr", out var compileStderr) && !string.IsNullOrWhiteSpace(compileStderr.ToString())) return true;
        if (root.TryGetProperty("stderr", out var stderr) && root.TryGetProperty("exitCode", out var exitCode) && exitCode.ValueKind == JsonValueKind.Number && exitCode.GetInt32() != 0 && !string.IsNullOrWhiteSpace(stderr.ToString())) return true;
        return false;
    }

    private static JsonElement? CloneJson(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try
        {
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.Clone();
        }
        catch
        {
            return null;
        }
    }

    private sealed record ClaimNextResponse(ExecutionJobDto? Job);
    private sealed record ExecutionJobDto(Guid Id, Guid SubmissionId, Guid? AssignmentId, Guid? UserId, string? Language, string? Code, string? Input, string? TestsJson, string? CodeForbiddenCallsJson, string? CodeRequiredCallsJson, int? TimeLimitMs, int? MemoryLimitMb, int AttemptCount, string Status);
    private sealed record AnalyzerRequest(string Language, string Source, [property: JsonPropertyName("extra_forbidden")] object? ExtraForbidden, [property: JsonPropertyName("forbidden_calls")] string[]? ForbiddenCalls, [property: JsonPropertyName("required_calls")] string[]? RequiredCalls);
    private sealed record RunnerRequest(string? Language, string? Code, string? Input, JsonElement[]? TestCases, JsonElement[]? Tests, int? TimeLimitMs, int? MemoryLimitMb);
    private sealed record CompleteExecutionJobRequest(string Status, string? Stdout, string? Stderr, int? ExitCode, long DurationMs, int Score, bool Passed, JsonElement? Result);
    private sealed record SolutionVerdictRequest(string Verdict, int Score, string Message, JsonElement? Result);

    private sealed record RunnerResult(string Verdict, int Score, bool Passed, JsonElement? Raw, JsonElement? Results, string? Stdout, string? Stderr, int? ExitCode, bool CompileError)
    {
        public string Message => Verdict switch
        {
            "Accepted" => "Все тесты пройдены.",
            "CompileError" => "Ошибка компиляции.",
            "PolicyFailed" => "Код содержит запрещённые конструкции.",
            "NoTestsConfigured" => "Для задания не настроены тесты.",
            "JudgeUnavailable" => "Judge pipeline временно недоступен.",
            _ => "Не все тесты пройдены."
        };

        public JsonElement? ToSolutionJson()
        {
            return CloneJson(JsonSerializer.Serialize(new
            {
                verdict = Verdict,
                score = Score,
                passedAllTests = Passed,
                compileError = CompileError,
                policyFailed = string.Equals(Verdict, "PolicyFailed", StringComparison.OrdinalIgnoreCase),
                message = Message,
                results = Results,
                cases = Results,
                raw = Raw
            }, JsonOptions));
        }

        public static RunnerResult NoTests() => new("NoTestsConfigured", 0, false, null, null, null, null, null, false);
        public static RunnerResult PolicyFailed(JsonElement raw, string message) => new("PolicyFailed", 0, false, raw, null, null, message, null, false);
        public static RunnerResult Error(string verdict, string message, JsonElement? raw = null) => new(verdict, 0, false, raw, null, null, message, null, false);
    }
}
