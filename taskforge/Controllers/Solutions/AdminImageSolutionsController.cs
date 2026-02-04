using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using taskforge.Data;

namespace taskforge.Controllers;

[ApiController]
[Route("api/admin")]
[Authorize(Roles = "Admin")]
public sealed class AdminImageSolutionsController : ControllerBase
{
    private readonly ApplicationDbContext _db;

    public AdminImageSolutionsController(ApplicationDbContext db)
    {
        _db = db;
    }

    public sealed record AdminImageSolutionListItem(
        Guid Id,
        Guid UserId,
        Guid AssignmentId,
        string AssignmentTitle,
        bool IsTrial,
        string Kind,
        string? Language,
        bool? Passed,
        double? SimilarityPercent,
        double? ThresholdPercent,
        DateTime CreatedAtUtc);

    public sealed record AdminImageSolutionDetails(
        Guid Id,
        Guid UserId,
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

    /// <summary>
    /// Список image-решений пользователя.
    /// GET /api/admin/users/{userId}/image-solutions?days=
    /// </summary>
    [HttpGet("users/{userId:guid}/image-solutions")]
    public async Task<ActionResult<List<AdminImageSolutionListItem>>> ListByUser([FromRoute] Guid userId, [FromQuery] int? days, CancellationToken ct)
    {
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

        var list = await q
            .OrderByDescending(x => x.CreatedAtUtc)
            .Select(x => new AdminImageSolutionListItem(
                x.Id,
                x.UserId,
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

    /// <summary>
    /// Детали одного image-решения.
    /// GET /api/admin/image-solutions/{id}
    /// </summary>
    [HttpGet("image-solutions/{id:guid}")]
    public async Task<ActionResult<AdminImageSolutionDetails>> Details([FromRoute] Guid id, CancellationToken ct)
    {
        var s = await _db.UserImageTaskSolutions
            .AsNoTracking()
            .Include(x => x.TaskAssignment)
            .FirstOrDefaultAsync(x => x.Id == id, ct);

        if (s is null) return NotFound();

        string? referenceUrl = string.IsNullOrWhiteSpace(s.ReferenceKey)
            ? null
            : $"/api/private-files/{Uri.EscapeDataString(s.ReferenceKey)}";

        string? submittedUrl = string.IsNullOrWhiteSpace(s.SubmittedKey)
            ? null
            : $"/api/private-files/{Uri.EscapeDataString(s.SubmittedKey)}";

        return Ok(new AdminImageSolutionDetails(
            s.Id,
            s.UserId,
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

    /// <summary>
    /// Удалить одно image-решение.
    /// DELETE /api/admin/image-solutions/{id}
    /// </summary>
    [HttpDelete("image-solutions/{id:guid}")]
    public async Task<IActionResult> Delete([FromRoute] Guid id, CancellationToken ct)
    {
        var s = await _db.UserImageTaskSolutions.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (s is null) return NotFound();
        _db.UserImageTaskSolutions.Remove(s);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }
}
