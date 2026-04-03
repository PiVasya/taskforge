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
        if (!IsAuthorizedInternal())
        {
            _log.LogWarning("Pull unauthorized for workerId={WorkerId}", request.WorkerId);
            return Unauthorized();
        }

        var job = await _jobs.PullNextAsync(request.WorkerId, request.Capabilities ?? new List<string>(), ct);
        if (job == null)
            return NoContent();

        _log.LogInformation("Pull → jobId={JobId} type={Type} priority={Priority}", job.Id, job.Type, job.Priority);
        return Ok(job);
    }

    [HttpPost("{jobId:guid}/heartbeat")]
    public async Task<IActionResult> Heartbeat(Guid jobId, [FromBody] AiWorkerHeartbeatRequestDto request, CancellationToken ct = default)
    {
        if (!IsAuthorizedInternal())
            return Unauthorized();

        var ok = await _jobs.HeartbeatAsync(jobId, request.WorkerId, ct);
        return ok ? Ok() : NotFound();
    }

    [HttpPost("{jobId:guid}/complete")]
    public async Task<IActionResult> Complete(Guid jobId, [FromBody] AiWorkerCompleteRequestDto request, CancellationToken ct = default)
    {
        if (!IsAuthorizedInternal())
        {
            _log.LogWarning("Complete unauthorized for jobId={JobId}", jobId);
            return Unauthorized();
        }

        _log.LogInformation("Complete → jobId={JobId} worker={WorkerId} model={Model} resultLen={Len}",
            jobId, request.WorkerId, request.ModelName, request.ResultJson?.Length ?? 0);
        var ok = await _jobs.CompleteAsync(jobId, request, ct);
        return ok ? Ok() : NotFound();
    }

    [HttpPost("{jobId:guid}/fail")]
    public async Task<IActionResult> Fail(Guid jobId, [FromBody] AiWorkerFailRequestDto request, CancellationToken ct = default)
    {
        if (!IsAuthorizedInternal())
            return Unauthorized();

        _log.LogWarning("Fail → jobId={JobId} worker={WorkerId} retryable={Retryable} error={Error}",
            jobId, request.WorkerId, request.Retryable, request.ErrorText?[..Math.Min(request.ErrorText.Length, 200)]);
        var ok = await _jobs.FailAsync(jobId, request, ct);
        return ok ? Ok() : NotFound();
    }

    private bool IsAuthorizedInternal()
    {
        var expectedKey = _config["API_INTERNAL_KEY"];
        var header = Request.Headers["X-Internal-Key"].ToString();
        return !string.IsNullOrEmpty(expectedKey) && header == expectedKey;
    }
}
