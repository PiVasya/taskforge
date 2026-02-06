using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using taskforge.Database;
using taskforge.DTO.Solutions;
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

    [HttpGet]
    public async Task<ActionResult<List<MyImageSolutionListItem>>> List(
        [FromQuery] int? days,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 50,
        CancellationToken ct = default)
    {
        if (skip < 0) skip = 0;
        if (take < 1) take = 1;
        if (take > 200) take = 200;

        var userId = _current.GetUserId();

        var q = _db.ImageSolutions.AsNoTracking().Where(s => s.UserId == userId);
        if (days is > 0)
        {
            var since = DateTime.UtcNow.AddDays(-days.Value);
            q = q.Where(s => s.CreatedAt >= since);
        }

        var items = await q
            .OrderByDescending(s => s.CreatedAt)
            .Skip(skip)
            .Take(take)
            .Select(s => new MyImageSolutionListItem
            {
                Id = s.Id,
                AssignmentId = s.AssignmentId,
                AssignmentTitle = s.Assignment.Title,
                Status = s.Status,
                Score = s.Score,
                Error = s.Error,
                CreatedAt = s.CreatedAt
            })
            .ToListAsync(ct);

        return Ok(items);
    }
}
