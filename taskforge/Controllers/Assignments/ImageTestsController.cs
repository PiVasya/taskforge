using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using taskforge.Constants;
using taskforge.Data;
using taskforge.Data.Models.Entities;
using taskforge.Services.Files;
using taskforge.Services.Interfaces;
using taskforge.Services.ImageTests;

namespace taskforge.Controllers.Assignments;

[ApiController]
[Route("api/assignments/{assignmentId:guid}/image-test")]
[Authorize]
public sealed class ImageTestsController : ControllerBase
{
    private readonly ApplicationDbContext _db;
    private readonly IFileStorageService _files;
    private readonly ICurrentUserService _current;
    private readonly IImageSimilarityService _sim;

    public ImageTestsController(ApplicationDbContext db, IFileStorageService files, ICurrentUserService current, IImageSimilarityService sim)
    {
        _db = db;
        _files = files;
        _current = current;
        _sim = sim;
    }

    [HttpPost("reference")]
    [RequestSizeLimit(10 * 1024 * 1024)]
    public async Task<IActionResult> UploadReference([FromRoute] Guid assignmentId, [FromForm] IFormFile file, [FromForm] double? threshold, CancellationToken ct)
    {
        if (file == null) return BadRequest(new { message = "Файл не передан" });

        var a = await _db.TaskAssignments
            .Include(x => x.Course)
            .FirstOrDefaultAsync(x => x.Id == assignmentId, ct);
        if (a == null) return NotFound(new { message = "Задание не найдено" });

        var userId = _current.GetUserId();
        var isOwner = a.Course.OwnerId == userId || await _db.CourseOwners.AnyAsync(o => o.CourseId == a.CourseId && o.UserId == userId, ct);
        if (!isOwner) return Forbid();

        a.Type = TaskAssignmentTypes.Normalize(a.Type);
        if (a.Type != TaskAssignmentTypes.ImageTest)
            return BadRequest(new { message = "Задание не является image-test" });

        var p = Math.Clamp(threshold ?? a.ImageTestSimilarityThreshold ?? 90, 0, 100);

        try
        {
            var prefix = $"image-tests/reference/{assignmentId:N}";
            var (key, _) = await _files.UploadImageAsync(file, prefix, ct);

            a.ImageTestReferenceKey = key;
            a.ImageTestSimilarityThreshold = p;
            a.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);

            return Ok(new
            {
                key,
                threshold = p,
                url = $"/api/private-files/{Uri.EscapeDataString(key)}"
            });
        }
        catch (ValidationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("compare")]
    [RequestSizeLimit(10 * 1024 * 1024)]
    public async Task<IActionResult> Compare([FromRoute] Guid assignmentId, [FromForm] IFormFile file, CancellationToken ct)
    {
        if (file == null) return BadRequest(new { message = "Файл не передан" });

        var a = await _db.TaskAssignments
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == assignmentId, ct);
        if (a == null) return NotFound(new { message = "Задание не найдено" });
        if (TaskAssignmentTypes.Normalize(a.Type) != TaskAssignmentTypes.ImageTest)
            return BadRequest(new { message = "Задание не является image-test" });
        if (string.IsNullOrWhiteSpace(a.ImageTestReferenceKey))
            return BadRequest(new { message = "Эталон не загружен" });

        var userId = _current.GetUserId();
        var threshold = Math.Clamp(a.ImageTestSimilarityThreshold ?? 90, 0, 100);

        try
        {
            var prefix = $"image-tests/attempts/{assignmentId:N}/{userId:N}";
            var (actualKey, _) = await _files.UploadImageAsync(file, prefix, ct);

            var (expectedStream, _) = await _files.GetAsync(a.ImageTestReferenceKey!, ct);
            await using var es = expectedStream;
            await using var act = file.OpenReadStream();

            var percent = await _sim.GetSimilarityPercentAsync(es, act, ct);
            var passed = percent >= threshold;

            return Ok(new
            {
                percent,
                passed,
                expectedUrl = $"/api/private-files/{Uri.EscapeDataString(a.ImageTestReferenceKey!)}",
                actualUrl = $"/api/private-files/{Uri.EscapeDataString(actualKey)}"
            });
        }
        catch (ValidationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }
}
