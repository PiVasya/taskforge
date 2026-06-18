using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Text.Json.Serialization;

namespace TaskForge.Execution.Worker;

public sealed partial class Worker(ILogger<Worker> logger, IHttpClientFactory httpClientFactory, IConfiguration configuration) : BackgroundService
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
        if (payload?.Job is not null)
        {
            logger.LogInformation("Claimed execution job {JobId} for submission {SubmissionId}, language {Language}.", payload.Job.Id, payload.Job.SubmissionId, payload.Job.Language);
        }
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

        // Publish the verdict before marking the execution job completed. If the
        // solutions-api call fails, the job stays running and will be re-queued by
        // execution-api's stale-running watchdog instead of leaving the submission
        // stuck in Queued/Running forever.
        await PublishVerdictAsync(job.SubmissionId, runnerResult, ct);
        await CompleteJobAsync(job.Id, complete, ct);
        logger.LogInformation("Execution job {JobId} completed with verdict {Verdict}, score {Score}.", job.Id, runnerResult.Verdict, runnerResult.Score);
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

            if (ok) return null;

            var clientPolicyPayload = BuildClientPolicyPayload(root);
            return RunnerResult.PolicyFailed(clientPolicyPayload, BuildPolicyMessage(clientPolicyPayload));
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

        // Go runners use json.Decoder.DisallowUnknownFields(), so the worker must not
        // send the broader internal RunnerRequest shape here. Sending fields such as
        // language, input or testCases to /run-tests makes cpp/java/js/pascal/python
        // runners reject the request with 400 before the code is executed.
        var payload = new RunnerTestsRequest(job.Code ?? string.Empty, tests, job.TimeLimitMs, job.MemoryLimitMb);
        var client = httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue("Judge:TimeoutSeconds", 45), 5, 180));

        try
        {
            logger.LogInformation("Dispatching execution job {JobId} to {Language} runner at {RunnerUrl} with {TestCount} tests.", job.Id, language, baseUrl, tests.Length);
            using var response = await client.PostAsJsonAsync($"{baseUrl}/run-tests", payload, JsonOptions, ct);
            var text = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                var safeText = SanitizeRunnerText(text);
                logger.LogWarning("Runner for job {JobId} returned status {StatusCode}: {Body}", job.Id, (int)response.StatusCode, Truncate(safeText, 500));
                return RunnerResult.Error("JudgeUnavailable", $"Runner returned {(int)response.StatusCode}.", CloneJson(safeText));
            }

            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
            var root = SanitizeRunnerPayload(doc.RootElement.Clone());
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
            var safeMessage = FriendlyRunnerText(ex.Message);
            return RunnerResult.Error("JudgeUnavailable", safeMessage, CloneJson(JsonSerializer.Serialize(new { error = safeMessage }, JsonOptions)));
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
        var attempts = Math.Clamp(configuration.GetValue("Judge:VerdictPublishAttempts", 5), 1, 10);
        var payload = new SolutionVerdictRequest(result.Verdict, result.Score, result.Message, result.ToSolutionJson());
        Exception? lastException = null;
        string? lastBody = null;

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            var client = httpClientFactory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{ServiceUrl("SolutionsApi", "http://solutions-api:8080")}/api/internal/solutions/submissions/{submissionId}/verdict")
            {
                Content = JsonContent.Create(payload, options: JsonOptions)
            };
            AddInternalKey(request);

            try
            {
                using var response = await client.SendAsync(request, ct);
                lastBody = await response.Content.ReadAsStringAsync(ct);
                if (response.IsSuccessStatusCode) return;
                logger.LogWarning("Publishing verdict for submission {SubmissionId} failed on attempt {Attempt}/{Attempts} with status {StatusCode}: {Body}", submissionId, attempt, attempts, response.StatusCode, Truncate(lastBody, 500));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastException = ex;
                logger.LogWarning(ex, "Publishing verdict for submission {SubmissionId} failed on attempt {Attempt}/{Attempts}.", submissionId, attempt, attempts);
            }

            if (attempt < attempts) await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), ct);
        }

        throw new InvalidOperationException($"Failed to publish verdict for submission {submissionId} after {attempts} attempts. Last body: {Truncate(lastBody, 500)}", lastException);
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
}
