using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using taskforge.Data;
using taskforge.Services.Interfaces;

namespace taskforge.Controllers;

/// <summary>
/// История image-решений текущего пользователя.
/// </summary>
[ApiController]
[Route("api/me/image-solutions")]
[Authorize]
public sealed class MyImageSolutionsController : ControllerBase
{
    private readonly ApplicationDbContext _db;
    private readonly ICurrentUserService _currentUser;

    public MyImageSolutionsController(ApplicationDbContext db, ICurrentUserService currentUser)
    {
        _db = db;
        _currentUser = currentUser;
    }

    public sealed record MyImageSolutionListItem(
        Guid Id,
        Guid AssignmentId,
        string AssignmentTitle,
        string Language,
        bool IsTrial,
        bool Passed,
        int SimilarityPercent,
        DateTime CreatedAtUtc
    );

    public sealed record MyImageSolutionDetails(
        Guid Id,
        Guid AssignmentId,
        string AssignmentTitle,
        string Language,
        bool IsTrial,
        bool Passed,
        int SimilarityPercent,
        int ThresholdPercent,
        string? RunnerError,
        string? Stdout,
        string? Stderr,
        string? SubmittedCode,
        string? ReferenceUrl,
        string? SubmittedUrl,
        DateTime CreatedAtUtc
    );

    private static int PercentToInt(double? value)
        => value is null ? 0 : (int)Math.Round(value.Value);

    /// <summary>
    /// Список image-решений текущего пользователя.
    /// days: сколько дней назад смотреть (по умолчанию 30). Если days=0 или меньше — без ограничения по дате.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<List<MyImageSolutionListItem>>> List(
        [FromQuery] int days = 30,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 50,
        [FromQuery] Guid? assignmentId = null,
        CancellationToken ct = default)
    {
        if (skip < 0) skip = 0;
        if (take <= 0) take = 50;
        if (take > 5000) take = 5000;

        var userId = _currentUser.GetUserId();

        var q = _db.UserImageTaskSolutions
            .AsNoTracking()
            .Include(x => x.TaskAssignment)
            .Where(x => x.UserId == userId);

        if (assignmentId.HasValue)
            q = q.Where(x => x.TaskAssignmentId == assignmentId.Value);

        if (days > 0)
        {
            var from = DateTime.UtcNow.AddDays(-days);
            q = q.Where(x => x.CreatedAtUtc >= from);
        }

        q = q.OrderByDescending(x => x.CreatedAtUtc);

        var items = await q
            .Skip(skip)
            .Take(take)
            .Select(x => new MyImageSolutionListItem(
                x.Id,
                x.TaskAssignmentId,
                x.TaskAssignment.Title,
                x.Language ?? string.Empty,
                x.IsTrial,
                x.Passed ?? false,
                PercentToInt(x.SimilarityPercent),
                x.CreatedAtUtc
            ))
            .ToListAsync(ct);

        return Ok(items);
    }

    /// <summary>
    /// Детали image-решения (только если оно принадлежит текущему пользователю).
    /// </summary>
    [HttpGet("{id:guid}")]
    public async Task<ActionResult<MyImageSolutionDetails>> Get(Guid id, CancellationToken ct = default)
    {
        var userId = _currentUser.GetUserId();

        var s = await _db.UserImageTaskSolutions
            .AsNoTracking()
            .Include(x => x.TaskAssignment)
            .FirstOrDefaultAsync(x => x.Id == id && x.UserId == userId, ct);

        if (s is null)
            return NotFound();

        // В сущности используются ReferenceKey / SubmittedKey
        string? referenceUrl = null;
        string? submittedUrl = s.SubmittedKey is null ? null : $"/api/private-files/{s.SubmittedKey}";

        return Ok(new MyImageSolutionDetails(
            s.Id,
            s.TaskAssignmentId,
            s.TaskAssignment.Title,
            s.Language ?? string.Empty,
            s.IsTrial,
            s.Passed ?? false,
            PercentToInt(s.SimilarityPercent),
            PercentToInt(s.ThresholdPercent),
            s.RunnerError,
            null,
            null,
            s.SubmittedCode,
            referenceUrl,
            submittedUrl,
            s.CreatedAtUtc
        ));
    }
}
