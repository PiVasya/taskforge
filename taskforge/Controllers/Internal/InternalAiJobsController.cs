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
        if (!IsAuthorizedInternal()) return Unauthorized();
        var job = await _jobs.PullNextAsync(request.WorkerId, request.Capabilities ?? new List<string>(), ct);
        return job == null ? NoContent() : Ok(job);
    }

    [HttpPost("{jobId:guid}/heartbeat")]
    public async Task<IActionResult> Heartbeat(Guid jobId, [FromBody] AiWorkerHeartbeatRequestDto request, CancellationToken ct = default)
    {
        if (!IsAuthorizedInternal()) return Unauthorized();
        var ok = await _jobs.HeartbeatAsync(jobId, request.WorkerId, ct);
        return ok ? Ok() : NotFound();
    }

    [HttpPost("{jobId:guid}/complete")]
    public async Task<IActionResult> Complete(Guid jobId, [FromBody] AiWorkerCompleteRequestDto request, CancellationToken ct = default)
    {
        if (!IsAuthorizedInternal()) return Unauthorized();
        var ok = await _jobs.CompleteAsync(jobId, request, ct);
        return ok ? Ok() : NotFound();
    }

    [HttpPost("{jobId:guid}/fail")]
    public async Task<IActionResult> Fail(Guid jobId, [FromBody] AiWorkerFailRequestDto request, CancellationToken ct = default)
    {
        if (!IsAuthorizedInternal()) return Unauthorized();
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
