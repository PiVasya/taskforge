using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using taskforge.Data.Models.DTO.AI;
using taskforge.Services.Interfaces;

namespace taskforge.Controllers.Internal;

[ApiController]
[Route("api/internal/ai/jobs")]
[AllowAnonymous]
public sealed class InternalAiJobsController : ControllerBase
{
    private readonly IConfiguration _config;
    private readonly IAiJobService _jobs;
    private readonly ILogger<InternalAiJobsController> _log;

    public InternalAiJobsController(IConfiguration config, IAiJobService jobs, ILogger<InternalAiJobsController> log)
    {
        _config = config;
        _jobs = jobs;
        _log = log;
    }

    [HttpPost("pull")]
    public async Task<IActionResult> Pull([FromBody] AiWorkerPullRequestDto request, CancellationToken ct = default)
    {
        Console.WriteLine($"[InternalAiJobsController] pull >>> workerId='{request.WorkerId}' caps='[{string.Join(",", request.Capabilities ?? new List<string>())}]' path='{Request.Path}'");
        if (!IsAuthorizedInternal())
        {
            _log.LogWarning("Pull unauthorized for workerId={WorkerId}", request.WorkerId);
            Console.WriteLine($"[InternalAiJobsController] pull <<< unauthorized workerId='{request.WorkerId}'");
            return Unauthorized();
        }

        var job = await _jobs.PullNextAsync(request.WorkerId, request.Capabilities ?? new List<string>(), ct);
        if (job == null)
        {
            Console.WriteLine($"[InternalAiJobsController] pull <<< no-content workerId='{request.WorkerId}'");
            return NoContent();
        }

        _log.LogInformation("Pull → jobId={JobId} type={Type} priority={Priority}", job.Id, job.Type, job.Priority);
        Console.WriteLine($"[InternalAiJobsController] pull <<< jobId={job.Id} type='{job.Type}' priority={job.Priority} stageCode='{job.StageCode}' stageLabel='{job.StageLabel}' targetType='{job.TargetEntityType}' targetId='{job.TargetEntityId}' courseId='{job.CourseId}'");
        return Ok(job);
    }

    [HttpPost("{jobId:guid}/heartbeat")]
    public async Task<IActionResult> Heartbeat(Guid jobId, [FromBody] AiWorkerHeartbeatRequestDto request, CancellationToken ct = default)
    {
        Console.WriteLine($"[InternalAiJobsController] heartbeat >>> jobId={jobId} workerId='{request.WorkerId}'");
        if (!IsAuthorizedInternal())
        {
            Console.WriteLine($"[InternalAiJobsController] heartbeat <<< unauthorized jobId={jobId} workerId='{request.WorkerId}'");
            return Unauthorized();
        }

        var ok = await _jobs.HeartbeatAsync(jobId, request.WorkerId, ct);
        Console.WriteLine($"[InternalAiJobsController] heartbeat <<< jobId={jobId} ok={ok}");
        return ok ? Ok() : NotFound();
    }

    [HttpPost("{jobId:guid}/complete")]
    public async Task<IActionResult> Complete(Guid jobId, [FromBody] AiWorkerCompleteRequestDto request, CancellationToken ct = default)
    {
        Console.WriteLine($"[InternalAiJobsController] complete >>> jobId={jobId} workerId='{request.WorkerId}' model='{request.ModelName}' resultLen={request.ResultJson?.Length ?? 0} telemetryLen={request.TelemetryJson?.Length ?? 0}");
        if (!IsAuthorizedInternal())
        {
            _log.LogWarning("Complete unauthorized for jobId={JobId}", jobId);
            Console.WriteLine($"[InternalAiJobsController] complete <<< unauthorized jobId={jobId}");
            return Unauthorized();
        }

        _log.LogInformation("Complete → jobId={JobId} worker={WorkerId} model={Model} resultLen={Len} telemetryLen={TelemetryLen}",
            jobId, request.WorkerId, request.ModelName, request.ResultJson?.Length ?? 0, request.TelemetryJson?.Length ?? 0);
        var ok = await _jobs.CompleteAsync(jobId, request, ct);
        Console.WriteLine($"[InternalAiJobsController] complete <<< jobId={jobId} ok={ok}");
        return ok ? Ok() : NotFound();
    }

    [HttpPost("{jobId:guid}/fail")]
    public async Task<IActionResult> Fail(Guid jobId, [FromBody] AiWorkerFailRequestDto request, CancellationToken ct = default)
    {
        Console.WriteLine($"[InternalAiJobsController] fail >>> jobId={jobId} workerId='{request.WorkerId}' retryable={request.Retryable} retryDelaySeconds={request.RetryDelaySeconds} error='{request.ErrorText}'");
        if (!IsAuthorizedInternal())
        {
            Console.WriteLine($"[InternalAiJobsController] fail <<< unauthorized jobId={jobId}");
            return Unauthorized();
        }

        _log.LogWarning("Fail → jobId={JobId} worker={WorkerId} retryable={Retryable} error={Error}",
            jobId, request.WorkerId, request.Retryable, request.ErrorText?[..Math.Min(request.ErrorText.Length, 200)]);
        var ok = await _jobs.FailAsync(jobId, request, ct);
        Console.WriteLine($"[InternalAiJobsController] fail <<< jobId={jobId} ok={ok}");
        return ok ? Ok() : NotFound();
    }

    private bool IsAuthorizedInternal()
    {
        var expectedKey = _config["API_INTERNAL_KEY"];
        var header = Request.Headers["X-Internal-Key"].ToString();
        return !string.IsNullOrEmpty(expectedKey) && header == expectedKey;
    }
}
