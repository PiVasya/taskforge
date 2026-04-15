using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using taskforge.Data.Models.DTO.AI;
using taskforge.Services.Interfaces;

namespace taskforge.Controllers.Admin;

[ApiController]
[Route("api/admin/ai")]
[Authorize(Roles = "Admin")]
public sealed class AdminAiController : ControllerBase
{
    private readonly IAiJobService _jobs;
    private readonly ICurrentUserService _current;

    public AdminAiController(IAiJobService jobs, ICurrentUserService current)
    {
        _jobs = jobs;
        _current = current;
    }

    [HttpGet("jobs")]
    public async Task<IActionResult> ListJobs([FromQuery] string? status, [FromQuery] string? type, [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default)
    {
        var data = await _jobs.GetAdminJobsAsync(status, type, page, pageSize, ct);
        return Ok(data);
    }

    [HttpGet("jobs/{id:guid}")]
    public async Task<IActionResult> GetJob(Guid id, CancellationToken ct = default)
    {
        var data = await _jobs.GetAdminJobAsync(id, ct);
        return data == null ? NotFound() : Ok(data);
    }

    [HttpPost("jobs")]
    public async Task<IActionResult> Enqueue([FromBody] CreateAiJobRequestDto request, CancellationToken ct = default)
    {
        var userId = _current.GetUserId();
        var name = User?.Identity?.Name;
        var data = await _jobs.EnqueueAsync(request, userId, name, ct);
        return CreatedAtAction(nameof(GetJob), new { id = data.Id }, data);
    }

    
    [HttpPost("batches/generate")]
    public async Task<IActionResult> GenerateBatch([FromBody] AiGenerateAssignmentBatchRequestDto request, CancellationToken ct = default)
    {
        var userId = _current.GetUserId();
        var name = User?.Identity?.Name;
        var data = await _jobs.QueueGenerateAssignmentBatchAsync(request, userId, name, ct);
        return CreatedAtAction(nameof(GetBatch), new { id = data.Id }, data);
    }

    [HttpGet("batches")]
    public async Task<IActionResult> GetBatches(CancellationToken ct = default)
    {
        var data = await _jobs.GetBatchesAsync(ct);
        return Ok(data);
    }

    [HttpGet("batches/{id:guid}")]
    public async Task<IActionResult> GetBatch(Guid id, CancellationToken ct = default)
    {
        var data = await _jobs.GetBatchAsync(id, ct);
        return data == null ? NotFound() : Ok(data);
    }

[HttpPost("generate/from-text")]
    public async Task<IActionResult> GenerateFromText([FromBody] AiGenerateAssignmentFromTextRequestDto request, CancellationToken ct = default)
    {
        var userId = _current.GetUserId();
        var name = User?.Identity?.Name;
        var data = await _jobs.QueueGenerateAssignmentFromTextAsync(request, userId, name, ct);
        return CreatedAtAction(nameof(GetJob), new { id = data.Id }, data);
    }

    [HttpPost("generate/from-file")]
    public async Task<IActionResult> GenerateFromFile([FromBody] AiGenerateAssignmentFromFileRequestDto request, CancellationToken ct = default)
    {
        var userId = _current.GetUserId();
        var name = User?.Identity?.Name;
        var data = await _jobs.QueueGenerateAssignmentFromFileAsync(request, userId, name, ct);
        return CreatedAtAction(nameof(GetJob), new { id = data.Id }, data);
    }

    [HttpPost("analyze-assignment")]
    public async Task<IActionResult> AnalyzeAssignment([FromBody] AiAnalyzeAssignmentRequestDto request, CancellationToken ct = default)
    {
        var userId = _current.GetUserId();
        var name = User?.Identity?.Name;
        var data = await _jobs.QueueAnalyzeAssignmentAsync(request, userId, name, ct);
        return data == null ? NotFound() : CreatedAtAction(nameof(GetJob), new { id = data.Id }, data);
    }

    [HttpPost("assignment-overviews/backfill")]
    public async Task<IActionResult> BackfillAssignmentOverviews([FromBody] AiBackfillAssignmentOverviewsRequestDto request, CancellationToken ct = default)
    {
        var userId = _current.GetUserId();
        var name = User?.Identity?.Name;
        var data = await _jobs.QueueAssignmentOverviewBackfillAsync(request, userId, name, ct);
        return Ok(new { count = data.Count, jobs = data });
    }

    [HttpPost("review-submission")]
    public async Task<IActionResult> ReviewSubmission([FromBody] AiReviewSubmissionRequestDto request, CancellationToken ct = default)
    {
        var userId = _current.GetUserId();
        var name = User?.Identity?.Name;
        var data = await _jobs.QueueReviewSubmissionAsync(request, userId, name, ct);
        return data == null ? NotFound() : CreatedAtAction(nameof(GetJob), new { id = data.Id }, data);
    }

    [HttpPost("review-user")]
    public async Task<IActionResult> ReviewUser([FromBody] AiReviewUserRequestDto request, CancellationToken ct = default)
    {
        var userId = _current.GetUserId();
        var name = User?.Identity?.Name;
        var data = await _jobs.QueueReviewUserAsync(request, userId, name, ct);
        return data == null ? NotFound() : CreatedAtAction(nameof(GetJob), new { id = data.Id }, data);
    }

    [HttpGet("submission-reviews")]
    public async Task<IActionResult> GetSubmissionReviews([FromQuery] Guid? userId, [FromQuery] Guid? assignmentId, CancellationToken ct = default)
    {
        var data = await _jobs.GetSubmissionReviewsAsync(userId, assignmentId, ct);
        return Ok(data);
    }

    [HttpGet("risk-reports")]
    public async Task<IActionResult> GetRiskReports([FromQuery] Guid? userId, CancellationToken ct = default)
    {
        var data = await _jobs.GetUserRiskReportsAsync(userId, ct);
        return Ok(data);
    }

    [HttpGet("assignment-insights")]
    public async Task<IActionResult> GetAssignmentInsights([FromQuery] Guid? assignmentId, CancellationToken ct = default)
    {
        var data = await _jobs.GetAssignmentInsightsAsync(assignmentId, ct);
        return Ok(data);
    }

    [HttpGet("drafts")]
    public async Task<IActionResult> GetDrafts(CancellationToken ct = default)
    {
        var data = await _jobs.GetDraftsAsync(ct);
        return Ok(data);
    }

    [HttpGet("drafts/{id:guid}")]
    public async Task<IActionResult> GetDraft(Guid id, CancellationToken ct = default)
    {
        var data = await _jobs.GetDraftAsync(id, ct);
        return data == null ? NotFound() : Ok(data);
    }

    [HttpPut("drafts/{id:guid}")]
    public async Task<IActionResult> UpdateDraft(Guid id, [FromBody] UpdateAiDraftRequestDto request, CancellationToken ct = default)
    {
        var data = await _jobs.UpdateDraftAsync(id, request, _current.GetUserId(), ct);
        return data == null ? NotFound() : Ok(data);
    }

    [HttpPost("drafts/{id:guid}/review")]
    public async Task<IActionResult> ReviewDraft(Guid id, [FromBody] ReviewAiDraftRequestDto request, CancellationToken ct = default)
    {
        var ok = await _jobs.ReviewDraftAsync(id, _current.GetUserId(), request.Action, ct);
        return ok ? Ok() : NotFound();
    }

    [HttpPost("drafts/{id:guid}/validate")]
    public async Task<IActionResult> ValidateDraft(Guid id, [FromBody] ValidateAiDraftRequestDto request, CancellationToken ct = default)
    {
        var data = await _jobs.QueueValidateDraftAsync(id, request, _current.GetUserId(), User?.Identity?.Name, ct);
        return data == null ? NotFound() : CreatedAtAction(nameof(GetJob), new { id = data.Id }, data);
    }

    [HttpPost("drafts/{id:guid}/publish")]
    public async Task<IActionResult> PublishDraft(Guid id, [FromBody] PublishAiDraftRequestDto request, CancellationToken ct = default)
    {
        var data = await _jobs.PublishDraftAsync(id, _current.GetUserId(), request, ct);
        return data == null ? NotFound() : Ok(data);
    }

    [HttpDelete("drafts/{id:guid}")]
    public async Task<IActionResult> DeleteDraft(Guid id, CancellationToken ct = default)
    {
        var ok = await _jobs.DeleteDraftAsync(id, ct);
        return ok ? NoContent() : NotFound();
    }

    [HttpDelete("batches/{id:guid}")]
    public async Task<IActionResult> DeleteBatch(Guid id, CancellationToken ct = default)
    {
        var ok = await _jobs.DeleteBatchAsync(id, ct);
        return ok ? NoContent() : NotFound();
    }

    [HttpDelete("jobs/{id:guid}")]
    public async Task<IActionResult> DeleteJob(Guid id, CancellationToken ct = default)
    {
        var ok = await _jobs.DeleteJobAsync(id, ct);
        return ok ? NoContent() : NotFound();
    }

    [HttpPost("jobs/clear")]
    public async Task<IActionResult> ClearJobs([FromQuery] string? status, CancellationToken ct = default)
    {
        var count = await _jobs.ClearJobsAsync(status, ct);
        return Ok(new { deleted = count });
    }

    [HttpPost("jobs/{id:guid}/retry")]
    public async Task<IActionResult> RetryJob(Guid id, CancellationToken ct = default)
    {
        var ok = await _jobs.RetryJobAsync(id, ct);
        return ok ? Ok() : NotFound();
    }

    [HttpPost("jobs/{id:guid}/cancel")]
    public async Task<IActionResult> CancelJob(Guid id, CancellationToken ct = default)
    {
        var ok = await _jobs.CancelJobAsync(id, ct);
        return ok ? Ok() : NotFound();
    }
}
