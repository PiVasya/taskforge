using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
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

    // Unique per process restart; the container hostname disambiguates cluster instances.
    private readonly string _workerInstance = BuildWorkerInstance();
    private DateTimeOffset _lastIdleReport = DateTimeOffset.MinValue;
    private int _emptyClaims;

    internal static string BuildWorkerInstance()
    {
        var configured = Environment.GetEnvironmentVariable("TASKFORGE_NODE_LABEL") ?? "unlabeled";
        var label = configured.Length is > 0 and <= 32 && configured.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')
            ? configured : "unlabeled";
        var host = Environment.MachineName;
        if (host.Length > 40) host = host[..40];
        return $"{label}/{host}/{Guid.NewGuid():N}";
    }

    private void AddWorkerHeaders(HttpRequestMessage request, ExecutionJobDto? job = null)
    {
        request.Headers.TryAddWithoutValidation("X-TaskForge-Execution-Worker", _workerInstance);
        if (job is null) return;
        request.Headers.TryAddWithoutValidation("X-TaskForge-Execution-Job", job.Id.ToString("D"));
        request.Headers.TryAddWithoutValidation("X-TaskForge-Execution-Attempt", job.AttemptCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("[TFQUEUE] WORKER_START worker={Worker} maxAttempts={MaxAttempts} runnerRetries={RunnerRetries} analyzerTimeoutSeconds={AnalyzerTimeoutSeconds} runnerTimeoutSeconds={RunnerTimeoutSeconds} completionRetries={CompletionRetries} verdictRetries={VerdictRetries}",
            _workerInstance, System.Math.Clamp(configuration.GetValue("ExecutionQueue:MaxAttempts", 3), 1, 10),
            System.Math.Clamp(configuration.GetValue("Judge:RunnerAttempts", 3), 1, 5),
            System.Math.Clamp(configuration.GetValue("CodeAnalyzer:TimeoutSeconds", 8), 2, 60),
            System.Math.Clamp(configuration.GetValue("Judge:TimeoutSeconds", 90), 80, 180),
            System.Math.Clamp(configuration.GetValue("Judge:CompletionAttempts", 5), 1, 10),
            System.Math.Clamp(configuration.GetValue("Judge:VerdictPublishAttempts", 5), 1, 10));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var job = await ClaimNextJobAsync(stoppingToken);
                if (job is null)
                {
                    _emptyClaims++;
                    if (DateTimeOffset.UtcNow >= _lastIdleReport.AddSeconds(60))
                    {
                        _lastIdleReport = DateTimeOffset.UtcNow;
                        logger.LogInformation("[TFQUEUE] WORKER_IDLE worker={Worker} emptyClaimsSincePreviousReport={EmptyClaims} pollIntervalSeconds=2",
                            _workerInstance, _emptyClaims);
                        _emptyClaims = 0;
                    }
                    await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                    continue;
                }

                _emptyClaims = 0;
                await ProcessJobAsync(job, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException)
            {
                logger.LogWarning("[TFQUEUE] WORKER_ATTEMPT_CANCELLED worker={Worker}; polling again", _workerInstance);
            }
            catch (ExecutionClaimLostException)
            {
                logger.LogWarning("[TFQUEUE] WORKER_ATTEMPT_EXPIRED worker={Worker}; polling again", _workerInstance);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[TFQUEUE] WORKER_LOOP_FAILED worker={Worker} errorType={ErrorType}", _workerInstance, ex.GetType().Name);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private async Task<ExecutionJobDto?> ClaimNextJobAsync(CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{ServiceUrl("ExecutionApi", "http://execution-api:8080")}/api/internal/execution/jobs/claim-next");
        AddInternalKey(request);
        AddWorkerHeaders(request);
        var claimStarted = Stopwatch.GetTimestamp();
        using var response = await client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("[TFQUEUE] CLAIM_HTTP_ERROR worker={Worker} status={StatusCode} elapsedMs={ElapsedMs:F1}",
                _workerInstance, (int)response.StatusCode, Stopwatch.GetElapsedTime(claimStarted).TotalMilliseconds);
            return null;
        }

        var payload = await response.Content.ReadFromJsonAsync<ClaimNextResponse>(JsonOptions, ct);
        if (payload?.Job is not null)
        {
            logger.LogInformation("[TFQUEUE] CLAIM_ACK worker={Worker} job={JobId} submission={SubmissionId} language={Language} attempt={Attempt} queueHttpMs={ClaimHttpMs:F1}",
                _workerInstance, payload.Job.Id, payload.Job.SubmissionId, payload.Job.Language,
                payload.Job.AttemptCount, Stopwatch.GetElapsedTime(claimStarted).TotalMilliseconds);
        }
        return payload?.Job;
    }

    private async Task ProcessJobAsync(ExecutionJobDto job, CancellationToken ct)
    {
        using var work = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var started = Stopwatch.StartNew();
        var stage = "prepare";
        var heartbeat = KeepClaimAliveAsync(job, work, ct, () => stage, started);
        logger.LogInformation("[TFQUEUE] WORK_START worker={Worker} job={JobId} submission={SubmissionId} assignment={AssignmentId} attempt={Attempt} language={Language}",
            _workerInstance, job.Id, job.SubmissionId, job.AssignmentId, job.AttemptCount, job.Language);
        try
        {
            var tests = ParseTests(job.TestsJson);
            RunnerResult runnerResult;
            logger.LogInformation("[TFQUEUE] TESTS_READY worker={Worker} job={JobId} attempt={Attempt} testCount={TestCount}",
                _workerInstance, job.Id, job.AttemptCount, tests.Length);
            if (tests.Length == 0)
            {
                runnerResult = RunnerResult.NoTests();
            }
            else
            {
                stage = "analyze";
                var analyzerStarted = Stopwatch.GetTimestamp();
                logger.LogInformation("[TFQUEUE] ANALYZER_START worker={Worker} job={JobId} attempt={Attempt} language={Language}",
                    _workerInstance, job.Id, job.AttemptCount, job.Language);
                var policy = await AnalyzePolicyAsync(job, work.Token);
                logger.LogInformation("[TFQUEUE] ANALYZER_DONE worker={Worker} job={JobId} attempt={Attempt} durationMs={DurationMs:F1} attested={Attested} blockedVerdict={BlockedVerdict}",
                    _workerInstance, job.Id, job.AttemptCount, Stopwatch.GetElapsedTime(analyzerStarted).TotalMilliseconds,
                    policy.Attestation.HasValue, policy.Block?.Verdict ?? "none");
                stage = "runner";
                runnerResult = policy.Block
                    ?? (policy.Attestation.HasValue
                        ? await RunTestsAsync(job, tests, policy.Attestation.Value, work.Token)
                        : RunnerResult.Error("JudgeUnavailable", "Сервис анализа кода не выдал обязательную подпись безопасности."));
            }

            stage = "claim-confirm";
            logger.LogInformation("[TFQUEUE] WORK_RESULT worker={Worker} job={JobId} submission={SubmissionId} attempt={Attempt} verdict={Verdict} score={Score} elapsedMs={ElapsedMs}",
                _workerInstance, job.Id, job.SubmissionId, job.AttemptCount, runnerResult.Verdict, runnerResult.Score, started.ElapsedMilliseconds);
            var complete = new CompleteExecutionJobRequest(
                Status: runnerResult.Verdict,
                Stdout: runnerResult.Stdout,
                Stderr: runnerResult.Stderr,
                ExitCode: runnerResult.ExitCode,
                DurationMs: started.ElapsedMilliseconds,
                Score: runnerResult.Score,
                Passed: runnerResult.Passed,
                Result: runnerResult.ToSolutionJson(),
                AttemptCount: job.AttemptCount);

            // Do not publish a late verdict after the job has been reassigned.
            await RenewClaimAsync(job, work.Token, requireConfirmation: true);
            work.Token.ThrowIfCancellationRequested();
            stage = "verdict-publish";
            logger.LogInformation("[TFQUEUE] VERDICT_SEND worker={Worker} job={JobId} submission={SubmissionId} attempt={Attempt} verdict={Verdict}",
                _workerInstance, job.Id, job.SubmissionId, job.AttemptCount, runnerResult.Verdict);
            await PublishVerdictAsync(job, runnerResult, work.Token);
            stage = "job-complete";
            logger.LogInformation("[TFQUEUE] COMPLETE_SEND worker={Worker} job={JobId} submission={SubmissionId} attempt={Attempt}",
                _workerInstance, job.Id, job.SubmissionId, job.AttemptCount);
            await CompleteJobAsync(job, complete, work.Token);
            stage = "finished";
            logger.LogInformation("[TFQUEUE] WORK_FINISHED worker={Worker} job={JobId} submission={SubmissionId} attempt={Attempt} verdict={Verdict} score={Score} elapsedMs={ElapsedMs}",
                _workerInstance, job.Id, job.SubmissionId, job.AttemptCount, runnerResult.Verdict, runnerResult.Score, started.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (work.IsCancellationRequested || ct.IsCancellationRequested)
        {
            logger.LogWarning("[TFQUEUE] WORK_CANCELLED worker={Worker} job={JobId} submission={SubmissionId} attempt={Attempt} stage={Stage} elapsedMs={ElapsedMs} shuttingDown={ShuttingDown}",
                _workerInstance, job.Id, job.SubmissionId, job.AttemptCount, stage, started.ElapsedMilliseconds, ct.IsCancellationRequested);
            throw;
        }
        catch (ExecutionClaimLostException)
        {
            logger.LogWarning("[TFQUEUE] WORK_CLAIM_LOST worker={Worker} job={JobId} submission={SubmissionId} attempt={Attempt} stage={Stage} elapsedMs={ElapsedMs}",
                _workerInstance, job.Id, job.SubmissionId, job.AttemptCount, stage, started.ElapsedMilliseconds);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[TFQUEUE] WORK_FAILED worker={Worker} job={JobId} submission={SubmissionId} attempt={Attempt} stage={Stage} elapsedMs={ElapsedMs} errorType={ErrorType}",
                _workerInstance, job.Id, job.SubmissionId, job.AttemptCount, stage, started.ElapsedMilliseconds, ex.GetType().Name);
            throw;
        }
        finally
        {
            await work.CancelAsync();
            try { await heartbeat; }
            catch (OperationCanceledException) when (work.IsCancellationRequested) { }
            logger.LogInformation("[TFQUEUE] WORK_EXIT worker={Worker} job={JobId} submission={SubmissionId} attempt={Attempt} lastStage={Stage} elapsedMs={ElapsedMs}",
                _workerInstance, job.Id, job.SubmissionId, job.AttemptCount, stage, started.ElapsedMilliseconds);
        }
    }

    private async Task KeepClaimAliveAsync(ExecutionJobDto job, CancellationTokenSource work, CancellationToken stoppingToken,
        Func<string> currentStage, Stopwatch started)
    {
        while (!work.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(20), work.Token);
                await RenewClaimAsync(job, work.Token, requireConfirmation: false);
                logger.LogInformation("[TFQUEUE] WORK_PROGRESS worker={Worker} job={JobId} submission={SubmissionId} attempt={Attempt} stage={Stage} elapsedSeconds={ElapsedSeconds}",
                    _workerInstance, job.Id, job.SubmissionId, job.AttemptCount, currentStage(), (long)started.Elapsed.TotalSeconds);
            }
            catch (OperationCanceledException) when (work.IsCancellationRequested || stoppingToken.IsCancellationRequested) { break; }
            catch (ExecutionClaimLostException)
            {
                logger.LogWarning("[TFQUEUE] HEARTBEAT_CLAIM_LOST worker={Worker} job={JobId} attempt={Attempt}; stopping stale attempt",
                    _workerInstance, job.Id, job.AttemptCount);
                await work.CancelAsync();
                break;
            }
            catch (Exception ex)
            {
                // A transient heartbeat error is not proof of lease loss; the
                // worker must still confirm ownership before publishing.
                logger.LogWarning(ex, "[TFQUEUE] HEARTBEAT_EXCEPTION worker={Worker} job={JobId} attempt={Attempt} errorType={ErrorType}",
                    _workerInstance, job.Id, job.AttemptCount, ex.GetType().Name);
            }
        }
    }

    private async Task RenewClaimAsync(ExecutionJobDto job, CancellationToken ct, bool requireConfirmation)
    {
        var client = httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(5);
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"{ServiceUrl("ExecutionApi", "http://execution-api:8080")}/api/internal/execution/jobs/{job.Id}/heartbeat")
        {
            Content = JsonContent.Create(new { attemptCount = job.AttemptCount }, options: JsonOptions)
        };
        AddInternalKey(request);
        AddWorkerHeaders(request, job);
        var started = Stopwatch.GetTimestamp();
        using var response = await client.SendAsync(request, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            logger.LogWarning("[TFQUEUE] HEARTBEAT_CONFLICT worker={Worker} job={JobId} attempt={Attempt} status=409",
                _workerInstance, job.Id, job.AttemptCount);
            throw new ExecutionClaimLostException();
        }
        // During rolling updates, the old execution-api does not yet have a
        // heartbeat route. Its legacy claim timeout still protects the queue.
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            if (requireConfirmation)
                logger.LogWarning("[TFQUEUE] HEARTBEAT_LEGACY_FALLBACK worker={Worker} job={JobId} attempt={Attempt} apiHasNoHeartbeat=true",
                    _workerInstance, job.Id, job.AttemptCount);
            return;
        }
        if (response.IsSuccessStatusCode)
            logger.LogDebug("[TFQUEUE] HEARTBEAT_ACK worker={Worker} job={JobId} attempt={Attempt} confirm={Confirm} elapsedMs={ElapsedMs:F1}",
                _workerInstance, job.Id, job.AttemptCount, requireConfirmation, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        if (!response.IsSuccessStatusCode)
        {
            if (requireConfirmation) response.EnsureSuccessStatusCode();
            logger.LogWarning("[TFQUEUE] HEARTBEAT_HTTP_ERROR worker={Worker} job={JobId} attempt={Attempt} httpStatus={Status} confirm={Confirm}",
                _workerInstance, job.Id, job.AttemptCount, (int)response.StatusCode, requireConfirmation);
        }
    }

    private sealed class ExecutionClaimLostException : Exception { }

    private async Task<PolicyAnalysisResult> AnalyzePolicyAsync(ExecutionJobDto job, CancellationToken ct)
    {
        if (!configuration.GetValue("CodeAnalyzer:Enabled", true))
        {
            return new PolicyAnalysisResult(
                RunnerResult.Error("JudgeUnavailable", "Обязательный сервис анализа кода отключён."),
                null);
        }

        var baseUrl = (configuration["CodeAnalyzer:Url"] ?? "http://code-analyzer:8080").TrimEnd('/');
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return new PolicyAnalysisResult(
                RunnerResult.Error("JudgeUnavailable", "Не настроен обязательный сервис анализа кода."),
                null);
        }

        var forbidden = ParseStringArray(job.CodeForbiddenCallsJson);
        var required = ParseStringArray(job.CodeRequiredCallsJson);
        var client = httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(System.Math.Clamp(configuration.GetValue("CodeAnalyzer:TimeoutSeconds", 8), 2, 60));

        var payload = new AnalyzerRequest(
            Language: NormalizeLanguage(job.Language),
            Profile: "standard",
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
                return new PolicyAnalysisResult(
                    failClosed
                        ? RunnerResult.Error("JudgeUnavailable", $"Code analyzer returned {(int)response.StatusCode}.", CloneJson(text))
                        : RunnerResult.Error("JudgeUnavailable", "Обязательная проверка кода не выполнена."),
                    null);
            }

            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
            var root = doc.RootElement.Clone();
            var ok = root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("ok", out var okProp)
                && okProp.ValueKind is JsonValueKind.True or JsonValueKind.False
                && okProp.GetBoolean();

            if (ok)
            {
                if (!root.TryGetProperty("attestation", out var attestation)
                    || attestation.ValueKind != JsonValueKind.Object)
                {
                    return new PolicyAnalysisResult(
                        RunnerResult.Error("JudgeUnavailable", "Code analyzer returned success without a signed attestation.", root),
                        null);
                }
                return new PolicyAnalysisResult(null, attestation.Clone());
            }

            var clientPolicyPayload = BuildClientPolicyPayload(root, job.Code ?? string.Empty);
            return new PolicyAnalysisResult(
                RunnerResult.PolicyFailed(clientPolicyPayload, BuildPolicyMessage(clientPolicyPayload)),
                null);
        }
        catch (Exception ex)
        {
            var failClosed = configuration.GetValue("CodeAnalyzer:FailClosed", true);
            return new PolicyAnalysisResult(
                failClosed
                    ? RunnerResult.Error("JudgeUnavailable", "Сервис анализа кода недоступен: " + ex.Message, CloneJson(JsonSerializer.Serialize(new { error = ex.Message }, JsonOptions)))
                    : RunnerResult.Error("JudgeUnavailable", "Обязательная проверка кода не выполнена."),
                null);
        }
    }

    private async Task<RunnerResult> RunTestsAsync(ExecutionJobDto job, JsonElement[] tests, JsonElement attestation, CancellationToken ct)
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
        object payload = language == "pascalabc"
            ? new PascalAbcRunnerTestsRequest(job.Code ?? string.Empty, "pascalabc", tests, job.TimeLimitMs, job.MemoryLimitMb, attestation)
            : new RunnerTestsRequest(job.Code ?? string.Empty, tests, job.TimeLimitMs, job.MemoryLimitMb, attestation);
        var client = httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(System.Math.Clamp(configuration.GetValue("Judge:TimeoutSeconds", 90), 80, 180));
        var attempts = System.Math.Clamp(configuration.GetValue("Judge:RunnerAttempts", 3), 1, 5);

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                logger.LogInformation(
                    "Dispatching execution job {JobId} to {Language} runner at {RunnerUrl} with {TestCount} tests (attempt {Attempt}/{Attempts}).",
                    job.Id, language, baseUrl, tests.Length, attempt, attempts);

                var runnerStarted = Stopwatch.GetTimestamp();
                using var runnerRequest = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/run-tests")
                {
                    Content = JsonContent.Create(payload, options: JsonOptions)
                };
                using var response = await client.SendAsync(runnerRequest, HttpCompletionOption.ResponseHeadersRead, ct);
                var text = await ReadBoundedRunnerResponseAsync(response.Content, ct);
                logger.LogInformation("[TFQUEUE] RUNNER_HTTP worker={Worker} job={JobId} submission={SubmissionId} attempt={Attempt} runnerAttempt={RunnerAttempt} language={Language} httpStatus={HttpStatus} elapsedMs={ElapsedMs:F1} responseBytes={ResponseBytes}",
                    _workerInstance, job.Id, job.SubmissionId, job.AttemptCount, attempt, language,
                    (int)response.StatusCode, Stopwatch.GetElapsedTime(runnerStarted).TotalMilliseconds, Encoding.UTF8.GetByteCount(text));
                if (!response.IsSuccessStatusCode)
                {
                    var safeText = SanitizeRunnerText(text);
                    logger.LogWarning(
                        "Runner for job {JobId} returned status {StatusCode} on attempt {Attempt}/{Attempts}: {Body}",
                        job.Id, (int)response.StatusCode, attempt, attempts, Truncate(safeText, 500));

                    if (attempt < attempts && IsTransientRunnerStatus(response.StatusCode))
                    {
                        await DelayRunnerRetryAsync(attempt, ct);
                        continue;
                    }

                    return RunnerResult.Error("JudgeUnavailable", $"Runner returned {(int)response.StatusCode}.", CloneJson(safeText));
                }

                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
                var root = SanitizeRunnerPayload(doc.RootElement.Clone());
                var results = ExtractResults(root);
                var hasResults = results.HasValue && results.Value.ValueKind == JsonValueKind.Array;
                var total = hasResults ? results!.Value.GetArrayLength() : 0;
                var passed = hasResults ? results!.Value.EnumerateArray().Count(IsPassedResult) : 0;
                var allPassed = total > 0 && passed == total;
                var judgeUnavailable = IsJudgeUnavailableRoot(root)
                    || (hasResults && results!.Value.EnumerateArray().Any(IsJudgeUnavailableResult));
                var incompleteSuccessfulBatch = hasResults && total > 0 && passed == total && total < tests.Length;
                var incompleteOrMalformedBatch = IsIncompleteRunnerBatch(root, results, tests.Length);

                if (judgeUnavailable || incompleteSuccessfulBatch || incompleteOrMalformedBatch)
                {
                    logger.LogWarning(
                        "Runner for job {JobId} reported an infrastructure or incomplete-batch failure on attempt {Attempt}/{Attempts}; the submission will not be classified as Rejected.",
                        job.Id, attempt, attempts);

                    if (attempt < attempts)
                    {
                        await DelayRunnerRetryAsync(attempt, ct);
                        continue;
                    }

                    return RunnerResult.Error("JudgeUnavailable", "Runner temporarily unavailable.", root);
                }

                var policyError = IsPolicyErrorRoot(root)
                    || (hasResults && results!.Value.EnumerateArray().Any(IsPolicyErrorResult));
                var compileError = !policyError && (IsCompileErrorRoot(root)
                    || (hasResults && results!.Value.EnumerateArray().Any(IsCompileErrorResult)));
                var timeLimit = IsTimeLimitRoot(root)
                    || (hasResults && results!.Value.EnumerateArray().Any(IsTimeLimitResult));
                var outputLimit = hasResults && results!.Value.EnumerateArray().Any(IsOutputLimitResult);
                // An explicit runner failure outranks a vacuous all-passed summary.
                var verdict = policyError ? "PolicyFailed" : timeLimit ? "TimeLimitExceeded" : outputLimit ? "OutputLimitExceeded" : compileError ? "CompileError" : allPassed ? "Accepted" : "Rejected";
                logger.LogInformation(
                    "[TFJUDGE OUTCOME] job={JobId} submission={SubmissionId} language={Language} attempt={Attempt} requested_tests={RequestedTests} completed_tests={CompletedTests} passed_tests={PassedTests} runner_duration_ms={RunnerDurationMs} configured_time_limit_ms={TimeLimitMs} configured_memory_limit_mb={MemoryLimitMb} response_bytes={ResponseBytes} verdict={Verdict} case_statuses={CaseStatuses}",
                    job.Id, job.SubmissionId, language, attempt, tests.Length, total, passed,
                    Stopwatch.GetElapsedTime(runnerStarted).TotalMilliseconds, job.TimeLimitMs, job.MemoryLimitMb,
                    Encoding.UTF8.GetByteCount(text), verdict, RunnerCaseStatusSummary(results));
                var score = ComputeRunnerScore(passed, tests.Length);
                var policyMessage = policyError ? "\u0420\u0435\u0448\u0435\u043d\u0438\u0435 \u043e\u0442\u043a\u043b\u043e\u043d\u0435\u043d\u043e \u0441\u0438\u0441\u0442\u0435\u043c\u043e\u0439 \u0431\u0435\u0437\u043e\u043f\u0430\u0441\u043d\u043e\u0441\u0442\u0438." : null;
                return new RunnerResult(verdict, score, allPassed, root, results, null, policyMessage, null, compileError);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (InvalidDataException ex)
            {
                logger.LogError(ex,
                    "[TFJUDGE OVERSIZED RESPONSE] job={JobId} language={Language} limit_bytes={LimitBytes}; no retry because the response violates the runner contract.",
                    job.Id, language, MaxRunnerResponseBytes);
                return RunnerResult.Error("JudgeUnavailable", "Runner returned a response larger than the safe limit.");
            }
            catch (Exception ex)
            {
                var safeMessage = FriendlyRunnerText(ex.Message);
                logger.LogWarning(ex,
                    "[TFQUEUE] RUNNER_EXCEPTION worker={Worker} job={JobId} submission={SubmissionId} attempt={Attempt} runnerAttempt={RunnerAttempt} runnerAttempts={RunnerAttempts} language={Language} errorType={ErrorType}",
                    _workerInstance, job.Id, job.SubmissionId, job.AttemptCount, attempt, attempts, language, ex.GetType().Name);

                if (attempt < attempts)
                {
                    await DelayRunnerRetryAsync(attempt, ct);
                    continue;
                }

                return RunnerResult.Error(
                    "JudgeUnavailable",
                    safeMessage,
                    CloneJson(JsonSerializer.Serialize(new { error = safeMessage }, JsonOptions)));
            }
        }

        return RunnerResult.Error("JudgeUnavailable", "Runner temporarily unavailable.");
    }

    internal const int MaxRunnerResponseBytes = 2 * 1024 * 1024;

    internal static async Task<string> ReadBoundedRunnerResponseAsync(HttpContent content, CancellationToken ct)
    {
        if (content.Headers.ContentLength is > MaxRunnerResponseBytes)
            throw new InvalidDataException("Runner response exceeds configured size limit.");

        await using var source = await content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[32 * 1024];
        int read;
        while ((read = await source.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaxRunnerResponseBytes)
                throw new InvalidDataException("Runner response exceeds configured size limit.");
            buffer.Write(chunk, 0, read);
        }
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    internal static string RunnerCaseStatusSummary(JsonElement? results)
    {
        if (!results.HasValue || results.Value.ValueKind != JsonValueKind.Array) return "none";
        var cases = new List<string>();
        var index = 0;
        foreach (var test in results.Value.EnumerateArray())
        {
            if (index >= 16) { cases.Add("more"); break; }
            var status = test.ValueKind == JsonValueKind.Object
                && test.TryGetProperty("status", out var value)
                ? value.ToString() : "unknown";
            if (status.Length > 30) status = status[..30];
            cases.Add($"{index}:{status}");
            index++;
        }
        return string.Join(",", cases);
    }

    internal static bool IsTransientRunnerStatus(System.Net.HttpStatusCode statusCode)
    {
        var code = (int)statusCode;
        return code == 408 || code == 429 || code >= 500;
    }

    private static Task DelayRunnerRetryAsync(int attempt, CancellationToken ct)
        => Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), ct);

    internal const int MaxInternalErrorResponseBytes = 4 * 1024;

    internal static async Task<string> ReadInternalErrorPreviewAsync(HttpContent content, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct);
        var preview = new byte[MaxInternalErrorResponseBytes];
        var count = 0;
        while (count < preview.Length)
        {
            var read = await stream.ReadAsync(preview.AsMemory(count), ct);
            if (read == 0) break;
            count += read;
        }
        return Encoding.UTF8.GetString(preview, 0, count);
    }

    private async Task CompleteJobAsync(ExecutionJobDto job, CompleteExecutionJobRequest requestBody, CancellationToken ct)
    {
        var jobId = job.Id;
        var attempts = System.Math.Clamp(configuration.GetValue("Judge:CompletionAttempts", 5), 1, 10);
        Exception? lastException = null;
        string? lastBody = null;

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            var client = httpClientFactory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{ServiceUrl("ExecutionApi", "http://execution-api:8080")}/api/internal/execution/jobs/{jobId}/complete")
            {
                Content = JsonContent.Create(requestBody, options: JsonOptions)
            };
            AddInternalKey(request);
            AddWorkerHeaders(request, job);

            try
            {
                var sentAt = Stopwatch.GetTimestamp();
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                if (response.IsSuccessStatusCode)
                {
                    logger.LogInformation("[TFQUEUE] COMPLETE_ACK worker={Worker} job={JobId} submission={SubmissionId} attempt={Attempt} httpStatus={HttpStatus} sendAttempt={SendAttempt} durationMs={DurationMs:F1}",
                        _workerInstance, jobId, job.SubmissionId, job.AttemptCount, (int)response.StatusCode, attempt,
                        Stopwatch.GetElapsedTime(sentAt).TotalMilliseconds);
                    return;
                }
                lastBody = await ReadInternalErrorPreviewAsync(response.Content, ct);

                logger.LogWarning(
                    "Completing execution job {JobId} failed on attempt {Attempt}/{Attempts} with status {StatusCode}: {Body}",
                    jobId, attempt, attempts, response.StatusCode, Truncate(lastBody, 500));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastException = ex;
                logger.LogWarning(ex, "Completing execution job {JobId} failed on attempt {Attempt}/{Attempts}.", jobId, attempt, attempts);
            }

            if (attempt < attempts) await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), ct);
        }

        throw new InvalidOperationException(
            $"Failed to complete execution job {jobId} after {attempts} attempts. Last body: {Truncate(lastBody, 500)}",
            lastException);
    }

    private async Task PublishVerdictAsync(ExecutionJobDto job, RunnerResult result, CancellationToken ct)
    {
        var submissionId = job.SubmissionId;
        var attempts = System.Math.Clamp(configuration.GetValue("Judge:VerdictPublishAttempts", 5), 1, 10);
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
            AddWorkerHeaders(request, job);

            try
            {
                var sentAt = Stopwatch.GetTimestamp();
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                if (response.IsSuccessStatusCode)
                {
                    logger.LogInformation("[TFQUEUE] VERDICT_ACK worker={Worker} job={JobId} submission={SubmissionId} attempt={Attempt} verdict={Verdict} httpStatus={HttpStatus} sendAttempt={SendAttempt} durationMs={DurationMs:F1}",
                        _workerInstance, job.Id, submissionId, job.AttemptCount, result.Verdict,
                        (int)response.StatusCode, attempt, Stopwatch.GetElapsedTime(sentAt).TotalMilliseconds);
                    return;
                }
                lastBody = await ReadInternalErrorPreviewAsync(response.Content, ct);
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
        "pascalabc" => configuration["Runners:Pascal"] ?? "http://pascal-runner:8080",
        "onec" => configuration["Runners:OneC"] ?? "http://onec-runner:8080",
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
        "onec" or "1c" or "1с" or "1c:enterprise" or "1с:предприятие" => "onec",
        "pas" or "pascal" => "pascal",
        "pascalabc" or "pascalabcnet" or "pascalabc.net" or "pabc" => "pascalabc",
        var x => x
    };
}
