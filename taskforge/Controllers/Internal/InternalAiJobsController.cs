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

    public InternalAiJobsController(IConfiguration config, IAiJobService jobs)
    {
        _config = config;
        _jobs = jobs;
    }

    [HttpPost("pull")]
    public async Task<IActionResult> Pull([FromBody] AiWorkerPullRequestDto request, CancellationToken ct = default)
    {
        Console.WriteLine($"[InternalAiJobsController] >>> pull workerId='{request.WorkerId}' capabilities='[{string.Join(",", request.Capabilities ?? new List<string>())}]'");
        if (!IsAuthorizedInternal())
        {
            Console.WriteLine("[InternalAiJobsController] <<< pull unauthorized");
            return Unauthorized();
        }

        var job = await _jobs.PullNextAsync(request.WorkerId, request.Capabilities ?? new List<string>(), ct);
        if (job == null)
        {
            Console.WriteLine($"[InternalAiJobsController] <<< pull no-content for workerId='{request.WorkerId}'");
            return NoContent();
        }

        Console.WriteLine($"[InternalAiJobsController] <<< pull jobId={job.Id} type='{job.Type}' priority={job.Priority} targetType='{job.TargetEntityType}' targetId='{job.TargetEntityId}'");
        return Ok(job);
    }

    [HttpPost("{jobId:guid}/heartbeat")]
    public async Task<IActionResult> Heartbeat(Guid jobId, [FromBody] AiWorkerHeartbeatRequestDto request, CancellationToken ct = default)
    {
        Console.WriteLine($"[InternalAiJobsController] >>> heartbeat jobId={jobId} workerId='{request.WorkerId}'");
        if (!IsAuthorizedInternal())
        {
            Console.WriteLine($"[InternalAiJobsController] <<< heartbeat unauthorized jobId={jobId}");
            return Unauthorized();
        }

        var ok = await _jobs.HeartbeatAsync(jobId, request.WorkerId, ct);
        Console.WriteLine($"[InternalAiJobsController] <<< heartbeat jobId={jobId} ok={ok}");
        return ok ? Ok() : NotFound();
    }

    [HttpPost("{jobId:guid}/complete")]
    public async Task<IActionResult> Complete(Guid jobId, [FromBody] AiWorkerCompleteRequestDto request, CancellationToken ct = default)
    {
        Console.WriteLine($"[InternalAiJobsController] >>> complete jobId={jobId} workerId='{request.WorkerId}' model='{request.ModelName}' resultJson.len={request.ResultJson?.Length ?? 0}");
        if (!IsAuthorizedInternal())
        {
            Console.WriteLine($"[InternalAiJobsController] <<< complete unauthorized jobId={jobId}");
            return Unauthorized();
        }

        var ok = await _jobs.CompleteAsync(jobId, request, ct);
        Console.WriteLine($"[InternalAiJobsController] <<< complete jobId={jobId} ok={ok}");
        return ok ? Ok() : NotFound();
    }

    [HttpPost("{jobId:guid}/fail")]
    public async Task<IActionResult> Fail(Guid jobId, [FromBody] AiWorkerFailRequestDto request, CancellationToken ct = default)
    {
        Console.WriteLine($"[InternalAiJobsController] >>> fail jobId={jobId} workerId='{request.WorkerId}' retryable={request.Retryable} retryDelaySeconds={request.RetryDelaySeconds} error.len={request.ErrorText?.Length ?? 0}");
        if (!IsAuthorizedInternal())
        {
            Console.WriteLine($"[InternalAiJobsController] <<< fail unauthorized jobId={jobId}");
            return Unauthorized();
        }

        var ok = await _jobs.FailAsync(jobId, request, ct);
        Console.WriteLine($"[InternalAiJobsController] <<< fail jobId={jobId} ok={ok}");
        return ok ? Ok() : NotFound();
    }

    private bool IsAuthorizedInternal()
    {
        var expectedKey = _config["API_INTERNAL_KEY"];
        var header = Request.Headers["X-Internal-Key"].ToString();
        var ok = !string.IsNullOrEmpty(expectedKey) && header == expectedKey;
        Console.WriteLine($"[InternalAiJobsController] auth check ok={ok} expectedConfigured={!string.IsNullOrEmpty(expectedKey)} headerPresent={!string.IsNullOrEmpty(header)} header.len={header.Length}");
        return ok;
    }
}
