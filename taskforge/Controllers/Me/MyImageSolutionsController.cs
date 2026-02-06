using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using taskforge.Data;
using taskforge.Services.Interfaces;

namespace taskforge.Controllers.Me;

[ApiController]
[Route("api/me/image-solutions")]
[Authorize]
public sealed class MyImageSolutionsController : ControllerBase
{
    private readonly ApplicationDbContext _db;
    private readonly ICurrentUserService _current;

    public MyImageSolutionsController(ApplicationDbContext db, ICurrentUserService current)
    {
        _db = db;
        _current = current;
    }

    public sealed record MyImageSolutionListItem(
        Guid Id,
        Guid AssignmentId,
        string AssignmentTitle,
        bool IsTrial,
        string Kind,
        string? Language,
        bool? Passed,
        double? SimilarityPercent,
        double? ThresholdPercent,
        DateTime CreatedAtUtc);

    public sealed record MyImageSolutionDetails(
        Guid Id,
        Guid AssignmentId,
        string AssignmentTitle,
        bool IsTrial,
        string Kind,
        string? Language,
        string? SubmittedCode,
        bool? Passed,
        double? SimilarityPercent,
        double? ThresholdPercent,
        string? ReferenceUrl,
        string? SubmittedUrl,
        string Stdout,
        string Stderr,
        string? RunnerError,
        DateTime CreatedAtUtc);

    [HttpGet]
    public async Task<ActionResult<List<MyImageSolutionListItem>>> List(
        [FromQuery] int? days,
        [FromQuery] Guid? assignmentId,
        [FromQuery] int? take,
        CancellationToken ct)
    {
        var userId = _current.GetUserId();
        var q = _db.UserImageTaskSolutions
            .AsNoTracking()
            .Where(x => x.UserId == userId)
            .Include(x => x.TaskAssignment)
            .AsQueryable();

        if (days.HasValue && days.Value > 0)
        {
            var from = DateTime.UtcNow.AddDays(-days.Value);
            q = q.Where(x => x.CreatedAtUtc >= from);
        }

        if (assignmentId.HasValue)
            q = q.Where(x => x.TaskAssignmentId == assignmentId.Value);

        var limit = Math.Clamp(take ?? 200, 1, 500);

        var list = await q
            .OrderByDescending(x => x.CreatedAtUtc)
            .Take(limit)
            .Select(x => new MyImageSolutionListItem(
                x.Id,
                x.TaskAssignmentId,
                x.TaskAssignment.Title,
                x.IsTrial,
                x.Kind,
                x.Language,
                x.Passed,
                x.SimilarityPercent,
                x.ThresholdPercent,
                x.CreatedAtUtc))
            .ToListAsync(ct);

        return Ok(list);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<MyImageSolutionDetails>> Details([FromRoute] Guid id, CancellationToken ct)
    {
        var userId = _current.GetUserId();

        var s = await _db.UserImageTaskSolutions
            .AsNoTracking()
            .Include(x => x.TaskAssignment)
            .FirstOrDefaultAsync(x => x.Id == id && x.UserId == userId, ct);

        if (s is null) return NotFound();

        string? referenceUrl = string.IsNullOrWhiteSpace(s.ReferenceKey)
            ? null
            : $"/api/private-files/{Uri.EscapeDataString(s.ReferenceKey)}";

        string? submittedUrl = string.IsNullOrWhiteSpace(s.SubmittedKey)
            ? null
            : $"/api/private-files/{Uri.EscapeDataString(s.SubmittedKey)}";

        return Ok(new MyImageSolutionDetails(
            s.Id,
            s.TaskAssignmentId,
            s.TaskAssignment.Title,
            s.IsTrial,
            s.Kind,
            s.Language,
            s.SubmittedCode,
            s.Passed,
            s.SimilarityPercent,
            s.ThresholdPercent,
            referenceUrl,
            submittedUrl,
            s.Stdout ?? string.Empty,
            s.Stderr ?? string.Empty,
            s.RunnerError,
            s.CreatedAtUtc));
    }
}
