using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TaskForge.Realtime;
using TaskForge.Solutions.Api.Contracts;
using TaskForge.Solutions.Api.Data;
using TaskForge.Solutions.Api.Domain;
using static TaskForge.Solutions.Api.Services.Access.SolutionsApiAccessService;
using static TaskForge.Solutions.Api.Services.Common.SolutionsApiCommonService;
using static TaskForge.Solutions.Api.Services.Mapping.SolutionsApiMappingService;
using static TaskForge.Solutions.Api.Services.Results.SolutionsApiResultsService;
using static TaskForge.Solutions.Api.Services.Serialization.SolutionsApiSerializationService;
using static TaskForge.Solutions.Api.Services.Testing.SolutionsApiTestingService;

namespace TaskForge.Solutions.Api.Services.Code;

internal static class CodeSolutionService
{
    internal static async Task<IResult> SubmitAsync(
        Guid assignmentId,
        SubmitRequest request,
        HttpContext http,
        IConfiguration cfg,
        SolutionsDbContext db,
        IHttpClientFactory httpFactory,
        AdminSolutionEventPublisher live,
        CancellationToken ct)
    {
        var queueLogger = http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("TaskForge.CodeQueue");
        if (CheckUserRateLimit(http, cfg, "solution-submit") is { } limited) return limited;
        var userId = CurrentUserId(http, cfg);
        if (userId == null) return Unauthorized();
        var unlimitedTaskEnergy = HasUnlimitedTaskEnergy(http, cfg);
        var canRevealHidden = IsEditor(http, cfg);

        var language = NormalizeLanguage(request.Language) ?? "csharp";
        var code = request.Code ?? string.Empty;
        if (string.IsNullOrWhiteSpace(code))
            return Problem(400, "SOLUTION_CODE_REQUIRED", "solutions.validation", "Нельзя отправить пустое решение.");

        var contract = await LoadCodeSolutionContractAsync(assignmentId, userId.Value, cfg, httpFactory, ct);
        if (contract is null)
        {
            if (!canRevealHidden)
                return Microsoft.AspNetCore.Http.Results.NotFound(new { message = "Задание не найдено.", code = "ASSIGNMENT_NOT_FOUND" });
            return Problem(503, "TASKS_CONTRACT_UNAVAILABLE", "solutions.tasks-contract", "Не удалось получить контракт code-задания из tasks-api.");
        }
        if (!canRevealHidden && !contract.CanSubmit)
            return Microsoft.AspNetCore.Http.Results.NotFound(new { message = "Задание не найдено.", code = "ASSIGNMENT_NOT_FOUND" });

        var spec = contract.Spec;
        if (!string.Equals(spec.Type, "code-test", StringComparison.OrdinalIgnoreCase))
            return Problem(409, "ASSIGNMENT_TYPE_MISMATCH", "solutions.validation", "Code solution можно отправить только для code-test задания.");

        var sub = new SolutionSubmission
        {
            AssignmentId = assignmentId,
            UserId = userId,
            Language = language,
            Code = code,
            Status = "Preparing",
            Score = 0,
            ResultJson = JsonSerializer.Serialize(new { verdict = "Preparing", message = "Решение принято и готовится к проверке." }, JsonOptions())
        };
        db.Submissions.Add(sub);
        await db.SaveChangesAsync(ct);
        queueLogger.LogInformation("[TFQUEUE] SUBMISSION_CREATED submission={SubmissionId} assignment={AssignmentId} language={Language} status={Status} createdAt={CreatedAt:O}",
            sub.Id, assignmentId, language, sub.Status, sub.CreatedAt);
        await live.PublishAsync("code", sub.Id, userId.Value, assignmentId, sub.Status, null);
        TaskForgeDebugTrace.Map("SUBMISSION_CREATED",
            ("submission", sub.Id),
            ("user", userId.Value),
            ("assignment", assignmentId),
            ("language", language),
            ("codeHash", TaskForgeDebugTrace.Fingerprint(code)),
            ("codeLength", code.Length),
            ("status", sub.Status));

        if (!IsAllowedLanguage(language, spec))
        {
            queueLogger.LogInformation("[TFQUEUE] SUBMISSION_LOCAL_VERDICT submission={SubmissionId} reason=language_not_allowed language={Language}",
                sub.Id, language);
            ApplyLocalVerdict(sub, new JudgeRunResult(
                "LanguageNotAllowed", 0, false, false,
                $"Язык {language} не разрешён для этого задания.",
                null,
                CloneJson(JsonSerializer.Serialize(new { language, allowedLanguages = EffectiveAllowedLanguages(spec) }, JsonOptions())),
                false));
            await db.SaveChangesAsync(ct);
            return Microsoft.AspNetCore.Http.Results.Ok(ToSubmitDto(sub, canRevealHidden));
        }

        var tests = ExtractTests(spec);
        if (tests.Length == 0)
        {
            queueLogger.LogWarning("[TFQUEUE] SUBMISSION_LOCAL_VERDICT submission={SubmissionId} reason=no_tests language={Language}",
                sub.Id, language);
            ApplyLocalVerdict(sub, new JudgeRunResult(
                "NoTestsConfigured", 0, false, false,
                "Для задания не настроены тесты, поэтому решение не может быть зачтено автоматически.",
                null, null, false));
            await db.SaveChangesAsync(ct);
            return Microsoft.AspNetCore.Http.Results.Ok(ToSubmitDto(sub, canRevealHidden));
        }

        var taskPolicy = QuotaPolicy(cfg, "tasks");
        if (!unlimitedTaskEnergy)
        {
            var quotaResult = await ConsumeQuotaAsync(db, userId.Value, "tasks", taskPolicy.Capacity, taskPolicy.Interval, 1, ct);
            WriteQuotaHeaders(http.Response, quotaResult.quota);
            if (!quotaResult.consumed)
            {
                db.Submissions.Remove(sub);
                await db.SaveChangesAsync(ct);
                return QuotaExceeded(quotaResult.quota);
            }
        }

        var enqueueStarted = Stopwatch.GetTimestamp();
        queueLogger.LogInformation("[TFQUEUE] ENQUEUE_START submission={SubmissionId} assignment={AssignmentId} language={Language} testCount={TestCount}",
            sub.Id, assignmentId, language, tests.Length);
        var enqueue = await EnqueueExecutionJobAsync(sub.Id, assignmentId, userId.Value, language, code, request.Input, tests, spec, cfg, httpFactory, ct);
        queueLogger.LogInformation("[TFQUEUE] ENQUEUE_RESULT submission={SubmissionId} job={JobId} created={Created} durationMs={DurationMs:F1}",
            sub.Id, enqueue.JobId, enqueue.Created, Stopwatch.GetElapsedTime(enqueueStarted).TotalMilliseconds);
        TaskForgeDebugTrace.Map("SUBMISSION_ENQUEUE",
            ("submission", sub.Id),
            ("user", userId.Value),
            ("assignment", assignmentId),
            ("created", enqueue.Created),
            ("executionJob", enqueue.JobId),
            ("message", enqueue.Message));
        if (!enqueue.Created)
        {
            queueLogger.LogWarning("[TFQUEUE] ENQUEUE_FAILED submission={SubmissionId} assignment={AssignmentId} durationMs={DurationMs:F1} outcome={Outcome}",
                sub.Id, assignmentId, Stopwatch.GetElapsedTime(enqueueStarted).TotalMilliseconds, enqueue.Message);
            if (!unlimitedTaskEnergy)
            {
                var refundedQuota = await RefundQuotaAsync(db, userId.Value, "tasks", taskPolicy.Capacity, taskPolicy.Interval, 1, ct);
                WriteQuotaHeaders(http.Response, refundedQuota);
            }
            var unavailableResult = new JudgeRunResult(
                "JudgeUnavailable", 0, false, false,
                enqueue.Message ?? "Execution pipeline временно недоступен.",
                null, enqueue.Raw, false);
            // The execution API can accept a job but lose the enqueue response.
            // Never overwrite a real verdict that arrived before the timeout.
            var fallback = new SolutionSubmission();
            ApplyLocalVerdict(fallback, unavailableResult);
            await db.Submissions.Where(x => x.Id == sub.Id && x.Status == "Preparing")
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.Status, fallback.Status)
                    .SetProperty(x => x.Score, fallback.Score)
                    .SetProperty(x => x.ResultJson, fallback.ResultJson), ct);
            db.Entry(sub).State = EntityState.Detached;
            var latest = await db.Submissions.AsNoTracking().SingleAsync(x => x.Id == sub.Id, ct);
            return Microsoft.AspNetCore.Http.Results.Ok(ToSubmitDto(latest, canRevealHidden));
        }

        var queuedPayload = JsonSerializer.Serialize(new
        {
            verdict = "Queued",
            status = "Queued",
            pending = true,
            executionJobId = enqueue.JobId,
            message = "Решение поставлено в очередь проверки. Результат появится после обработки execution-worker."
        }, JsonOptions());
        // The worker may publish its terminal verdict immediately after enqueue.
        // A tracked entity SaveChanges here would overwrite that verdict with Queued.
        var pendingChanged = await db.Submissions.Where(x => x.Id == sub.Id && x.Status == "Preparing")
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Status, "Queued")
                .SetProperty(x => x.Score, 0)
                .SetProperty(x => x.ResultJson, queuedPayload), ct);
        queueLogger.LogInformation("[TFQUEUE] SUBMISSION_QUEUED submission={SubmissionId} job={JobId} changed={Changed} alreadyTerminal={AlreadyTerminal}",
            sub.Id, enqueue.JobId, pendingChanged, pendingChanged == 0);
        db.Entry(sub).State = EntityState.Detached;

        var waitStarted = Stopwatch.GetTimestamp();
        var final = await WaitForTerminalSubmissionAsync(db, sub.Id, cfg, ct);
        var returned = final ?? await db.Submissions.AsNoTracking().SingleAsync(x => x.Id == sub.Id, ct);
        queueLogger.LogInformation("[TFQUEUE] SUBMISSION_RETURN submission={SubmissionId} job={JobId} status={Status} score={Score} terminal={Terminal} waitedMs={WaitedMs:F1} ageSec={AgeSec}",
            sub.Id, enqueue.JobId, returned.Status, returned.Score, IsTerminalVerdict(returned.Status),
            Stopwatch.GetElapsedTime(waitStarted).TotalMilliseconds, Math.Max(0, (long)(DateTimeOffset.UtcNow - returned.CreatedAt).TotalSeconds));
        TaskForgeDebugTrace.Map("SUBMISSION_RETURN",
            ("submission", sub.Id),
            ("user", userId.Value),
            ("assignment", assignmentId),
            ("status", returned.Status),
            ("score", returned.Score),
            ("terminalObserved", IsTerminalVerdict(final?.Status)));
        return Microsoft.AspNetCore.Http.Results.Ok(ToSubmitDto(returned, canRevealHidden));
    }
}
