using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
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
        if (root.ValueKind != JsonValueKind.Object)
        {
            return "Код содержит запрещённые конструкции.";
        }

        var kind = ReadString(root, "policyKind") ?? string.Empty;
        if (string.Equals(kind, "platform", StringComparison.OrdinalIgnoreCase))
        {
            return "Решение отклонено системой безопасности.";
        }

        if (!root.TryGetProperty("errors", out var errors) || errors.ValueKind != JsonValueKind.Array)
        {
            return "Код содержит запрещённые конструкции.";
        }

        var visibleErrors = errors.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.Object)
            .Where(e => !IsSensitivePolicyPattern(ReadString(e, "pattern_id") ?? ReadString(e, "patternId")))
            .ToArray();

        var lines = visibleErrors
            .Select(e => ReadString(e, "message"))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToArray();

        if (visibleErrors.Any(IsCyrillicPolicyError))
        {
            return lines.Length == 0
                ? "В исполняемом коде найдена кириллица. Используйте латинские имена переменных, функций и классов."
                : string.Join("; ", lines);
        }

        return lines.Length == 0
            ? "Код содержит запрещённые конструкции."
            : "Код содержит запрещённые конструкции: " + string.Join("; ", lines);
    }


    private static bool IsCyrillicPolicyError(JsonElement error)
    {
        if (error.ValueKind != JsonValueKind.Object) return false;
        var code = ReadString(error, "code") ?? string.Empty;
        var patternId = ReadString(error, "pattern_id") ?? ReadString(error, "patternId") ?? string.Empty;
        var message = ReadString(error, "message") ?? string.Empty;
        return code.Contains("cyrillic", StringComparison.OrdinalIgnoreCase)
            || string.Equals(patternId, "unicode.cyrillic_in_code", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Кириллиц", StringComparison.OrdinalIgnoreCase);
    }

    private static JsonElement BuildClientPolicyPayload(JsonElement root)
    {
        var visibleErrors = new List<Dictionary<string, object?>>();
        var visibleHits = new List<Dictionary<string, object?>>();
        var visiblePatternIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sensitiveCount = 0;

        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in errors.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object) continue;

                var code = ReadString(e, "code") ?? "policy_failed";
                var message = ReadString(e, "message") ?? "Код содержит запрещённые конструкции.";
                var patternId = ReadString(e, "pattern_id") ?? ReadString(e, "patternId") ?? "-";

                if (IsSensitivePolicyPattern(patternId))
                {
                    sensitiveCount++;
                    continue;
                }

                visiblePatternIds.Add(patternId);
                visibleErrors.Add(new Dictionary<string, object?>
                {
                    ["code"] = code,
                    ["message"] = message,
                    ["pattern_id"] = patternId
                });
            }
        }

        if (sensitiveCount > 0)
        {
            visibleErrors.Add(new Dictionary<string, object?>
            {
                ["code"] = "sandbox_security",
                ["message"] = visibleErrors.Count == 0
                    ? "Код использует системные возможности, которые нельзя запускать в песочнице."
                    : "Дополнительно код использует системные возможности, которые нельзя запускать в песочнице.",
                ["pattern_id"] = "platform.security"
            });
        }

        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("hits", out var hits) && hits.ValueKind == JsonValueKind.Array)
        {
            foreach (var h in hits.EnumerateArray())
            {
                if (h.ValueKind != JsonValueKind.Object) continue;
                var patternId = ReadString(h, "pattern_id") ?? ReadString(h, "patternId") ?? "-";
                if (IsSensitivePolicyPattern(patternId)) continue;
                if (visiblePatternIds.Count > 0 && !visiblePatternIds.Contains(patternId)) continue;

                visibleHits.Add(new Dictionary<string, object?>
                {
                    ["pattern_id"] = patternId,
                    ["needle"] = ReadString(h, "needle") ?? string.Empty,
                    ["position"] = ReadInt(h, "position"),
                    ["preview"] = ReadString(h, "preview") ?? string.Empty
                });
            }
        }

        static bool IsVisibleTaskError(Dictionary<string, object?> e)
        {
            return !e.TryGetValue("pattern_id", out var pattern)
                || !string.Equals(pattern?.ToString(), "platform.security", StringComparison.OrdinalIgnoreCase);
        }

        var policyKind = visibleErrors.Any(IsVisibleTaskError)
            ? (sensitiveCount > 0 ? "mixed" : "task")
            : "platform";

        return CloneJson(JsonSerializer.Serialize(new
        {
            ok = false,
            policyKind,
            sensitivePlatformViolations = sensitiveCount,
            errors = visibleErrors,
            hits = visibleHits
        }, JsonOptions))!.Value;
    }

    private static bool IsSensitivePolicyPattern(string? patternId)
    {
        var id = (patternId ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(id) || id == "-") return false;

        return id.StartsWith("py.")
            || id.StartsWith("js.")
            || id.StartsWith("c.")
            || id.StartsWith("cpp.")
            || id.StartsWith("cs.")
            || id.StartsWith("java.")
            || id.StartsWith("pas.");
    }

    private static string? ReadString(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.ToString(),
            _ => null
        };
    }

    private static int? ReadInt(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var i)) return i;
        return null;
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
            var merged = new List<JsonElement>();
            if (element.TryGetProperty("publicTests", out var publicTests) && publicTests.ValueKind == JsonValueKind.Array)
                merged.AddRange(publicTests.EnumerateArray().Select(x => NormalizeTestCase(x, hidden: false)));
            if (element.TryGetProperty("hiddenTests", out var hiddenTests) && hiddenTests.ValueKind == JsonValueKind.Array)
                merged.AddRange(hiddenTests.EnumerateArray().Select(x => NormalizeTestCase(x, hidden: true)));
            if (merged.Count > 0) return merged.ToArray();

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

    private static JsonElement NormalizeTestCase(JsonElement item, bool hidden)
    {
        if (item.ValueKind != JsonValueKind.Object) return item.Clone();
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            input = ReadString(item, "input") ?? ReadString(item, "stdin") ?? string.Empty,
            expectedOutput = ReadString(item, "expectedOutput") ?? ReadString(item, "expected") ?? ReadString(item, "stdout") ?? string.Empty,
            isHidden = hidden || ReadBool(item, "isHidden") || ReadBool(item, "hidden")
        }, JsonOptions));
        return doc.RootElement.Clone();
    }

    private static bool ReadBool(JsonElement item, string name)
        => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False && value.GetBoolean();


    private static readonly Regex TmpTaskforgePathRegex = new(@"/tmp/taskforge-[^\s:]+", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex TmpGoBuildPathRegex = new(@"/tmp/go-build[^\s:]+", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex AppPathRegex = new(@"/app/[^\s:]+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static string SanitizeRunnerText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return value ?? string.Empty;
        var s = TmpTaskforgePathRegex.Replace(value, "[временный файл]");
        s = TmpGoBuildPathRegex.Replace(s, "[временный файл]");
        s = AppPathRegex.Replace(s, "[внутренний файл]");
        return s;
    }

    private static string FriendlyRunnerText(string? value)
    {
        var s = SanitizeRunnerText(value);
        if (s.Contains("fork/exec", StringComparison.OrdinalIgnoreCase)
            && s.Contains("permission denied", StringComparison.OrdinalIgnoreCase))
        {
            return "Не удалось запустить программу: нет прав на выполнение файла проверки.";
        }
        return s;
    }

    private static JsonElement SanitizeRunnerPayload(JsonElement root)
    {
        try
        {
            var node = JsonNode.Parse(root.GetRawText());
            if (node is null) return root;
            SanitizeRunnerNode(node);
            return CloneJson(node.ToJsonString(JsonOptions)) ?? root;
        }
        catch
        {
            return root;
        }
    }

    private static void SanitizeRunnerNode(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var kv in obj.ToList())
                {
                    if (kv.Value is JsonValue value && value.TryGetValue<string>(out var textValue))
                    {
                        if (ShouldSanitizeRunnerField(kv.Key)) obj[kv.Key] = FriendlyRunnerText(textValue);
                    }
                    else
                    {
                        SanitizeRunnerNode(kv.Value);
                    }
                }
                break;
            case JsonArray arr:
                foreach (var child in arr)
                {
                    SanitizeRunnerNode(child);
                }
                break;
        }
    }

    private static bool ShouldSanitizeRunnerField(string? name)
    {
        var key = (name ?? string.Empty).Trim().ToLowerInvariant();
        return key == "stderr"
            || key == "compilestderr"
            || key == "compileerror"
            || key == "error"
            || key == "message"
            || key == "detail"
            || key == "errormessage"
            || key.Contains("exception")
            || key.Contains("stacktrace");
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

    private static string Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Length <= maxLength ? value : value[..maxLength] + "...";
    }

    private sealed record ClaimNextResponse(ExecutionJobDto? Job);
    private sealed record ExecutionJobDto(Guid Id, Guid SubmissionId, Guid? AssignmentId, Guid? UserId, string? Language, string? Code, string? Input, string? TestsJson, string? CodeForbiddenCallsJson, string? CodeRequiredCallsJson, int? TimeLimitMs, int? MemoryLimitMb, int AttemptCount, string Status);
    private sealed record AnalyzerRequest(string Language, string Source, [property: JsonPropertyName("extra_forbidden")] object? ExtraForbidden, [property: JsonPropertyName("forbidden_calls")] string[]? ForbiddenCalls, [property: JsonPropertyName("required_calls")] string[]? RequiredCalls);
    private sealed record RunnerTestsRequest(string Code, JsonElement[] Tests, int? TimeLimitMs, int? MemoryLimitMb);
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
